using System;
using System.Linq;
using System.Threading.Tasks;
using LanguageExt;
using Llama.Airforce.Database.Contexts;
using Llama.Airforce.Domain.Models;
using Llama.Airforce.Jobs.Extensions;
using Llama.Airforce.Jobs.Factories;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;
using static LanguageExt.Prelude;

namespace Llama.Airforce.Functions;

public class Dashboards
{
    private readonly IWeb3 Web3;
    private readonly BribesContext BribesContext;
    private readonly DashboardContext DashboardContext;

    public Dashboards(
        IWeb3 web3,
        BribesContext bribesContext,
        DashboardContext dashboardContext)
    {
        Web3 = web3;
        BribesContext = bribesContext;
        DashboardContext = dashboardContext;
    }

    [FunctionName("Dashboards")]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *", RunOnStartup = false)] TimerInfo dashboardsTimer,
        ILogger log)
    {
        // Get Votium data.
        var epochsVotium = await BribesContext
            .GetAllAsync(
                Platform.Votium.ToPlatformString(),
                Protocol.ConvexCrv.ToProtocolString())
            .Map(toList);

        var latestFinishedEpochVotium = epochsVotium
            .OrderBy(epoch => epoch.End)
            .Last(epoch => epoch.End <= DateTime.UtcNow.ToUnixTimeSeconds());

        var votiumData = new DashboardFactory.VotiumData(
            epochsVotium,
            latestFinishedEpochVotium);

        // Get Aura data.
        var epochsAura = await BribesContext
            .GetAllAsync(
                Platform.HiddenHand.ToPlatformString(),
                Protocol.AuraBal.ToProtocolString())
            .Map(toList);

        var latestFinishedEpochAura = epochsAura
            .OrderBy(epoch => epoch.End)
            .Last(epoch => epoch.End <= DateTime.UtcNow.ToUnixTimeSeconds());

        var auraData = new DashboardFactory.AuraData(
            epochsAura,
            latestFinishedEpochAura);

        await Jobs.Jobs.Dashboards.UpdateDashboards(
            log,
            Web3,
            DashboardContext,
            votiumData,
            auraData);
    }
}