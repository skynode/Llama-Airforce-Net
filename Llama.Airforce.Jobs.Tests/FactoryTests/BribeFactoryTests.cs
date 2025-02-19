﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Llama.Airforce.Domain.Models;
using Llama.Airforce.Jobs.Extensions;
using Llama.Airforce.Jobs.Factories;
using Llama.Airforce.Jobs.Functions;
using Llama.Airforce.Jobs.Snapshots.Models;
using Llama.Airforce.SeedWork.Types;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;
using NUnit.Framework;
using static LanguageExt.Prelude;

namespace Llama.Airforce.Jobs.Tests.FactoryTests;

public class BribeFactoryTests
{
    [Test]
    [TestCase(Platform.Votium, Protocol.ConvexCrv, 275218.498948121, 4073133.7953767194)]
    [TestCase(Platform.HiddenHand, Protocol.AuraBal, 5111.9299885981391, 326374.53677203262)]
    public async Task ProcessEpoch(
        Platform platform,
        Protocol protocol,
        double bribes,
        double bribed)
    {
        // Arrange
        var web3 = new Web3(Constants.ALCHEMY);
        var logger = new LoggerFactory().CreateLogger("test");
        var getPrice = fun((Proposal proposal, Address tokenAddress, string token) =>
        {
            // Try to get the price at the end of the day of the deadline.
            // If that fails, try to get a fallback price.
            // If the proposal ends later than now (ongoing proposal), we take current prices.
            var time = DateTimeExt.FromUnixTimeSeconds(proposal.End);
            time = time > DateTime.UtcNow ? DateTime.UtcNow : time;

            return PriceFunctions.GetPriceExt(
                tokenAddress,
                PriceFunctions.GetNetwork(token),
                web3,
                time,
                token);
        });

        // Act
        var bribeFunctions = BribesFactory.GetBribesFunctions(platform, protocol);

        var proposalIds = await bribeFunctions.GetProposalIds()
            .MatchAsync(x => x, _ => throw new Exception());

        var epochs = await bribeFunctions.GetEpochs()
            .MatchAsync(x => x, _ => throw new Exception());

        var epoch = epochs.First();
        var dbEpoch = await BribesFactory.ProcessEpoch(
                logger,
                web3,
                new BribesFactory.OptionsProcessEpoch(
                    bribeFunctions,
                    platform,
                    protocol,
                    proposalIds,
                    epoch,
                    0),
                getPrice)
            .MatchAsync(x => x, _ => throw new Exception());

        // Assert
        Assert.AreEqual(bribes, dbEpoch.Bribes.Sum(bribe => bribe.AmountDollars));
        Assert.AreEqual(bribed, dbEpoch.Bribed.Sum(x => x.Value));
    }
}