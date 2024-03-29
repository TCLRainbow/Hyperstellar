using ClashOfClans;
using ClashOfClans.Core;
using ClashOfClans.Models;
using ClashOfClans.Search;
using Hyperstellar.Discord;
using Hyperstellar.Sql;
using QuikGraph;
using QuikGraph.Algorithms.MaximumFlow;

namespace Hyperstellar.Clash;

internal static class Coc
{
    private sealed class RaidAttackerComparer : IEqualityComparer<ClanCapitalRaidSeasonAttacker>
    {
        public bool Equals(ClanCapitalRaidSeasonAttacker? x, ClanCapitalRaidSeasonAttacker? y) => x!.Tag == y!.Tag;
        public int GetHashCode(ClanCapitalRaidSeasonAttacker obj) => obj.Tag.GetHashCode();
    }

    private const string ClanId = "#2QU2UCJJC"; // 2G8LP8PVV
    private static readonly ClashOfClansClient s_client = new(Secrets.s_coc);
    private static ClashOfClansException? s_exception;
    private static ClanCapitalRaidSeason s_raidSeason;
    internal static ClanUtil Clan { get; private set; } = new();
    internal static event Action<ClanMember, Main> EventMemberJoined;
    internal static event Action<ClanMember, string?> EventMemberLeft;
    internal static event Action<ClanCapitalRaidSeason> EventInitRaid;
    internal static event Action<ClanCapitalRaidSeason> EventRaidCompleted;
    internal static event Action<IEnumerable<Tuple<string, int>>> EventDonatedMaxFlow;
    internal static event Func<IEnumerable<Tuple<string, int>>, IEnumerable<Tuple<string, int>>, Task> EventDonated;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    static Coc() => Dc.EventBotReady += BotReadyAsync;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    private static void CheckMembersJoined(ClanUtil clan)
    {
        if (clan._joiningMembers.Count == 0)
        {
            return;
        }

        string[] members = [.. clan._joiningMembers.Keys];
        Db.AddMembers(members);
        string membersMsg = string.Join(", ", members);
        Console.WriteLine($"{membersMsg} joined");

        foreach (ClanMember m in clan._joiningMembers.Values)
        {
            Main main = new(m.Tag);
            EventMemberJoined(m, main);
            main.Insert();
        }
    }

    private static void CheckMembersLeft(ClanUtil clan)
    {
        if (clan._leavingMembers.Count == 0)
        {
            return;
        }

        foreach ((string id, ClanMember member) in clan._leavingMembers)
        {
            Member fakeMem = new(id);
            IEnumerable<Alt> alts = fakeMem.GetAltsByMain();
            string? altId = null;
            if (alts.Any())
            {
                Alt alt = alts.First();
                altId = alt.AltId;
                for (int i = 1; i < alts.Count(); i++)
                {
                    alts.ElementAt(i).UpdateMain(alt.AltId);
                }
                alt.Delete();
                // Maybe adapt this in the future if need to modify attributes when replacing main
                Main main = fakeMem.ToMain();
                main.Delete();
                main.MainId = altId;
                main.Insert();
            }
            // This is before Db.DelMem below so that we can remap Donation to new mainId
            // ^ No longer true because the remap is done ABOVE now but I'll still leave this comment
            EventMemberLeft(member, altId);
        }

        string[] members = [.. clan._leavingMembers.Keys];
        Db.DeleteMembers(members);
        string membersMsg = string.Join(", ", members);
        Console.WriteLine($"{membersMsg} left");
    }

    private static async Task BotReadyAsync()
    {
        while (true)
        {
            try
            {
                await PollAsync();
                s_exception = null;
                await Task.Delay(10000);
            }
            catch (ClashOfClansException ex)
            {
                if (s_exception == null || s_exception.Error.Reason != ex.Error.Reason || s_exception.Error.Message != ex.Error.Message)
                {
                    s_exception = ex;
                    await Dc.ExceptionAsync(ex);
                }
                await Task.Delay(60000);
            }
            catch (Exception ex)
            {
                await Dc.ExceptionAsync(ex);
                await Task.Delay(60000);
            }
        }
    }

    private static async Task<Clan> GetClanAsync() => await s_client.Clans.GetClanAsync(ClanId);

    private static async Task PollAsync()
    {
        Clan clan = await GetClanAsync();

        if (clan.MemberList == null)
        {
            return;
        }

        ClanUtil clanUtil = ClanUtil.FromPoll(clan);

        CheckMembersJoined(clanUtil);
        CheckMembersLeft(clanUtil);
        await Task.WhenAll([
            CheckDonationsAsync(clanUtil)
        ]);
        Clan = clanUtil;
    }

    private static async Task PollRaidAsync()
    {
        static async Task WaitRaidAsync()
        {
            await Task.Delay(s_raidSeason.EndTime - DateTime.UtcNow);
            s_raidSeason = await GetRaidSeasonAsync();
            while (s_raidSeason.State != ClanCapitalRaidSeasonState.Ended)
            {
                await Task.Delay(20000);
                s_raidSeason = await GetRaidSeasonAsync();
            }
            EventRaidCompleted(s_raidSeason);
        }

        // Check if there is an ongoing raid
        if (s_raidSeason.EndTime > DateTime.UtcNow)
        {
            await WaitRaidAsync();
        }
        while (true)
        {
            await Task.Delay(60 * 60 * 1000); // 1 hour
            ClanCapitalRaidSeason season = await GetRaidSeasonAsync();
            if (season.StartTime != s_raidSeason.StartTime) // New season started
            {
                s_raidSeason = season;
                await WaitRaidAsync();
            }
        }
    }

    private static async Task CheckDonationsAsync(ClanUtil clan)
    {
        List<Tuple<string, int>> donDelta = [], recDelta = [];
        Dictionary<string, string> accToMainAcc = [];
        AdjacencyGraph<string, TaggedEdge<string, int>> graph = new(false);
        graph.AddVertexRange(["s", "t"]);

        foreach (string tag in clan._existingMembers.Keys)
        {
            ClanMember current = clan._members[tag];
            ClanMember previous = Clan._members[tag];

            if (current.Donations > previous.Donations)
            {
                int donated = current.Donations - previous.Donations;

                donDelta.Add(new(current.Tag, donated));

                graph.AddVertex(current.Tag);
                graph.AddEdge(new("s", current.Tag, donated));

                accToMainAcc.TryAdd(current.Tag, new Member(current.Tag).GetEffectiveMain().MainId);
            }
        }

        foreach (string tag in clan._existingMembers.Keys)
        {
            ClanMember current = clan._members[tag];
            ClanMember previous = Clan._members[tag];

            if (current.DonationsReceived > previous.DonationsReceived)
            {
                string vertexName = $"#{current.Tag}"; // Double # for received node
                int received = current.DonationsReceived - previous.DonationsReceived;
                recDelta.Add(new(current.Tag, received));
                graph.AddVertex(vertexName);
                graph.AddEdge(new(vertexName, "t", received));

                foreach (string donor in accToMainAcc
                    .Where(kv => kv.Value != new Member(current.Tag).GetEffectiveMain().MainId)
                    .Select(kv => kv.Key))
                {
                    graph.AddEdge(new(donor, vertexName, received));
                }
            }
        }

        if (graph.VertexCount > 2)
        {
            ReversedEdgeAugmentorAlgorithm<string, TaggedEdge<string, int>> reverseAlgo = new(
                graph,
                (s, t) =>
                {
                    TaggedEdge<string, int> e = graph.Edges.First(e => e.Source == t && e.Target == s);
                    return new TaggedEdge<string, int>(s, t, e.Tag);
                });
            reverseAlgo.AddReversedEdges();

            EdmondsKarpMaximumFlowAlgorithm<string, TaggedEdge<string, int>> maxFlowAlgo = new(
                graph,
                e => e.Tag, // capacities
                (_, _) => graph.Edges.First(), // EdgeFactory (isn't actually used by the algo)
                reverseAlgo)
            {
                Source = "s",
                Sink = "t"
            };
            maxFlowAlgo.Compute();

            List<Tuple<string, int>> maxFlowDonations = [];
            foreach ((TaggedEdge<string, int> edge, double capa) in
                maxFlowAlgo.ResidualCapacities.Where(ed => ed.Key.Source == "s"))
            {
                int donated = (int)(edge.Tag - capa);
                if (donated > 0)
                {
                    maxFlowDonations.Add(new(edge.Target, donated));
                }
            }

            if (maxFlowDonations.Count > 0)
            {
                EventDonatedMaxFlow(maxFlowDonations);
            }
            await EventDonated(donDelta, recDelta);
        }
    }

    internal static string? GetMemberId(string name)
    {
        ClanMember? result = Clan._clan.MemberList!.FirstOrDefault(m => m.Name == name);
        return result?.Tag;
    }

    internal static ClanMember GetMember(string id) => Clan._members[id];

    internal static HashSet<ClanCapitalRaidSeasonAttacker> GetRaidAttackers(ClanCapitalRaidSeason season)
    {
        HashSet<ClanCapitalRaidSeasonAttacker> set = new(new RaidAttackerComparer());
        foreach (ClanCapitalRaidSeasonAttackLogEntry capital in season.AttackLog)
        {
            foreach (ClanCapitalRaidSeasonDistrict district in capital.Districts)
            {
                if (district.Attacks != null)
                {
                    foreach (ClanCapitalRaidSeasonAttack atk in district.Attacks)
                    {
                        set.Add(atk.Attacker);
                    }
                }
            }
        }
        return set;
    }

    internal static async Task<ClanCapitalRaidSeason> GetRaidSeasonAsync()
    {
        Query query = new() { Limit = 1 };
        ClanCapitalRaidSeasons seasons = (ClanCapitalRaidSeasons)await s_client.Clans.GetCapitalRaidSeasonsAsync(ClanId, query);
        return seasons.First();
    }

    internal static async Task InitAsync()
    {
        static async Task InitClanAsync() => Clan = ClanUtil.FromInit(await GetClanAsync());
        static async Task InitRaidAsync()
        {
            ClanCapitalRaidSeason season = await GetRaidSeasonAsync();
            // If last raid happened within a week, we count it as valid
            EventInitRaid(season);
            s_raidSeason = season;
            _ = Task.Run(PollRaidAsync);
        }

        await Task.WhenAll([InitClanAsync(), InitRaidAsync()]);
    }
}
