﻿using System.Numerics;
using LanguageExt;
using LanguageExt.Common;
using LanguageExt.UnsafeValueAccess;
using Llama.Airforce.Database.Models.Bribes;
using Llama.Airforce.Domain.Models;
using Llama.Airforce.Jobs.Extensions;
using Llama.Airforce.SeedWork.Extensions;
using Llama.Airforce.SeedWork.Types;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;
using static LanguageExt.Prelude;
using Db = Llama.Airforce.Database.Models;
using Dom = Llama.Airforce.Domain.Models;
using Snap = Llama.Airforce.Jobs.Snapshots.Models;

namespace Llama.Airforce.Jobs.Factories;

public static class BribesFactory
{
    public record OptionsGetBribes(
        Platform Platform,
        Protocol Protocol,
        bool LastEpochOnly);

    public record BribesFunctions(
        Func<EitherAsync<Error, Map<string, (int, string)>>> GetProposalIds,
        Func<EitherAsync<Error, Lst<Dom.Epoch>>> GetEpochs,
        Func<Lst<Address>, BigInteger, EitherAsync<Error, Map<Address, double>>> GetScores);

    public static Error CreateError((Platform Platform, Protocol Protocol) x) => Error
        .New($"The combination of {x.Platform.ToPlatformString()}-{x.Protocol.ToProtocolString()} is not valid");

    public static BribesFunctions GetBribesFunctions(Platform platform, Protocol protocol) =>
        (platform, protocol) switch
        {
            (Platform.Votium, Protocol.ConvexCrv) => new BribesFunctions(
                Snapshots.Convex.GetProposalIds,
                Subgraphs.Votium.GetEpochs,
                Snapshots.Convex.GetScores),

            (Platform.HiddenHand, Protocol.AuraBal) => new BribesFunctions(
                Snapshots.Aura.GetProposalIds,
                fun(() => Snapshots.Aura.GetProposalIds()
                    .Map(x => x
                        .Values
                        .OrderBy(proposal => proposal.Index)
                        .ToList())
                    .Bind(Subgraphs.HiddenHand.GetEpochs)),
                Snapshots.Aura.GetScores),

            _ => new BribesFunctions(
                () => EitherAsync<Error, Map<string, (int, string)>>.Left(CreateError((platform, protocol))),
                () => EitherAsync<Error, Lst<Dom.Epoch>>.Left(CreateError((platform, protocol))),
                (_, _) => EitherAsync<Error, Map<Address, double>>.Left(CreateError((platform, protocol))))
        };

    public static Func<
            ILogger,
            IWeb3,
            OptionsGetBribes,
            Func<Snap.Proposal, Address, string, EitherAsync<Error, double>>,
            EitherAsync<Error, Lst<Db.Bribes.Epoch>>>
        GetBribes = fun((
            ILogger logger,
            IWeb3 web3,
            OptionsGetBribes options,
            Func<Snap.Proposal, Address, string, EitherAsync<Error, double>> getPrice) =>
        {
            var bribeFunctions = GetBribesFunctions(options.Platform, options.Protocol);

            var proposalIds_ = bribeFunctions.GetProposalIds();
            var epochs_ = bribeFunctions.GetEpochs();

            EitherAsync<Error, EitherAsync<Error, Lst<Db.Bribes.Epoch>>> dbEpochs;
            if (options.LastEpochOnly)
            {
                dbEpochs =
                    from proposalIds in proposalIds_
                    from epochs in epochs_
                    select epochs
                        .Reverse()
                        .Take(1)
                        .Map(epoch => ProcessEpoch(
                            logger,
                            web3,
                            new OptionsProcessEpoch(
                                bribeFunctions,
                                options.Platform,
                                options.Protocol,
                                proposalIds,
                                epoch,
                            epochs.Count - 1),
                            getPrice))
                        .SequenceSerial()
                        .Map(toList);
            }
            else
            {
                dbEpochs =
                    from proposalIds in proposalIds_
                    from epochs in epochs_
                    select epochs
                        .Map((i, epoch) => ProcessEpoch(
                            logger,
                            web3,
                            new OptionsProcessEpoch(
                                bribeFunctions,
                                options.Platform,
                                options.Protocol,
                                proposalIds,
                                epoch,
                                i),
                            getPrice))
                        .SequenceSerial()
                        .Map(toList);
            }

            return dbEpochs.Bind(x => x);
        });

    public record OptionsProcessEpoch(
        BribesFunctions BribesFunctions,
        Platform Platform,
        Protocol Protocol,
        Map<string, (int Index, string Id)> ProposalIds,
        Dom.Epoch Epoch,
        int Index);

    public static Func<
            ILogger,
            IWeb3,
            OptionsProcessEpoch,
            Func<Snap.Proposal, Address, string, EitherAsync<Error, double>>,
            EitherAsync<Error, Db.Bribes.Epoch>>
        ProcessEpoch = fun((
            ILogger logger,
            IWeb3 web3,
            OptionsProcessEpoch options,
            Func<Snap.Proposal, Address, string, EitherAsync<Error, double>> getPrice) =>
        {
            var epochId = EpochId.Create(
                StringMax.Of(options.Platform.ToPlatformString()).ValueUnsafe(),
                StringMax.Of(options.Protocol.ToProtocolString()).ValueUnsafe(),
                options.Index + 1);

            logger.LogInformation($"Updating bribes: {epochId}");

            var epoch = options.Epoch;

            var proposalId_ = options.ProposalIds
                .Find(x => x.Value.Id == epoch.SnapshotId)
                .Map(x => x.Key)
                .ToAsync()
                .ToEither(Error.New($"Failed to find id for proposal {epoch.SnapshotId}"));

            var proposal_ = proposalId_.Bind(Snapshots.Snapshot.GetProposal);

            var bribes_ = proposal_.Bind(proposal => epoch
                .Bribes
                // Process each bribe.
                .Map(par(ProcessBribe, logger, web3, proposal, par(getPrice, proposal)))
                // Transform the list of tasks to a task of a list.
                .SequenceSerial()
                .Map(toList));

            var bribeChoices_ =
                from proposal in proposal_
                from bribes in bribes_
                select bribes
                    .Distinct()
                    .Map(bribe => (
                        Pool: proposal.Choices[bribe.Choice],
                        // We need to undo the Snapshot index offset.
                        Choice: (bribe.Choice + 1).ToString()))
                    .toList();

            var votes_ = proposal_.Bind(proposal => Snapshots
                .Snapshot
                .GetVotes(proposal.Id));

            var snapshot_ = proposal_.MapTry(proposal => BigInteger.Parse(proposal.Snapshot));

            var scores_ = (
                from votes in votes_
                from snapshot in snapshot_
                select options.BribesFunctions.GetScores(
                    votes.Map(vote => Address.Of(vote.Voter)).Somes().toList(),
                    snapshot))
                .Bind(x => x);

            var votesPools_ =
                from proposalId in proposalId_
                from votes in votes_
                from scores in scores_
                from bribeChoices in bribeChoices_
                select votes
                    .Aggregate(
                        Map<string, double>(),
                        (acc, vote) =>
                        {
                            var voteTotal = vote.Choices.Values.Sum();

                            // It's possible people vote on 'nothing' through Snapshot.
                            if (voteTotal == 0) return acc;

                            var voter = Address.Of(vote.Voter).ValueUnsafe();
                            var voteWeight = scores.Find(voter).ValueUnsafe();

                            // Fix for the mim/mim-ust round 3 fuckup.
                            // Add mim votes to mim-ust pool.
                            if (proposalId == "QmaS9vd1vJKQNBYX4KWQ3nppsTT3QSL3nkz5ZYSwEJk6hZ" && vote.Choices.ContainsKey("41"))
                            {
                                vote.Choices["52"] = vote.Choices.GetValueOrDefault("52") + vote.Choices["41"];
                                vote.Choices["41"] = 0;
                            }

                            // Only bother with choices that can be bribed.
                            var choices = vote.Choices
                                .Map(x => bribeChoices
                                    .Find(b => b.Choice == x.Key)
                                    .Map(y => (y.Pool, Score: x.Value)))
                                .Somes();

                            foreach (var choice in choices)
                            {
                                var scoreNormalized = choice.Score / voteTotal;
                                var scoreWeighted = voteWeight * scoreNormalized;
                                acc = acc.AddOrUpdate(choice.Pool, x => x + scoreWeighted, scoreWeighted);
                            }

                            return acc;
                        });

            return from proposal in proposal_
                   from bribes in bribes_
                   from votesPools in votesPools_
                   select new Db.Bribes.Epoch
                   {
                       Platform = options.Platform.ToPlatformString(),
                       Protocol = options.Protocol.ToProtocolString(),
                       Round = options.Index + 1,
                       End = proposal.End,
                       Proposal = proposal.Id,
                       Bribed = votesPools.ToDictionary(),
                       Bribes = bribes.ToList()
                   };
        });

    public static Func<
            ILogger,
            IWeb3,
            Snap.Proposal,
            Func<Address, string, EitherAsync<Error, double>>,
            Dom.Bribe,
            EitherAsync<Error, Db.Bribes.Bribe>>
        ProcessBribe = fun((
            ILogger logger,
            IWeb3 web3,
            Snap.Proposal proposal,
            Func<Address, string, EitherAsync<Error, double>> getPrice,
            Dom.Bribe bribe) =>
        {
            var tokenAddress = Address.Of(bribe.Token).ValueUnsafe();
            var token_ = Contracts.ERC20.GetSymbol(web3, tokenAddress).ToEitherAsync();
            var decimals_ = Contracts.ERC20.GetDecimals(web3, tokenAddress).ToEitherAsync();

            var amount_ = decimals_.Map(decimals => BigInteger.Parse(bribe.Amount).DivideByDecimals(decimals));
            var pool_ = Try(() => proposal.Choices[bribe.Choice])
                .ToAsync()
                .ToEither(ex =>
                    Error.New($"Choice index {bribe.Choice} was not found in the list of choices for proposal {proposal.Id}", ex));

            // Convert any price error to $0, but log it. Then convert to EitherAsync for the applicative below.
            var price_ = token_.Bind(token => getPrice(tokenAddress, token))
                .Match(
                    Right: x => x,
                    Left: ex =>
                    {
                        logger.LogError(ex.Message);
                        return 0;
                    })
                .ToEitherAsync();

            return
                from price in price_
                from pool in pool_
                from token in token_
                from amount in amount_
                select new Db.Bribes.Bribe
                {
                    Pool = pool,
                    Token = token,
                    Choice = bribe.Choice,
                    Amount = amount,
                    AmountDollars = amount * price
                };
        });
}