﻿using Hyperstellar.Sql;
using Hyperstellar.Discord;
using ClashOfClans.Models;

namespace Hyperstellar.Clash;

internal static class Donate25
{
    private sealed class Node(long time)
    {
        internal long _checkTime = time;
        internal readonly ICollection<string> _ids = [];
    }

    private const int TargetPerPerson = 25; // The donation target per week per person
    private const long CheckPeriod = 7 * 24 * 3600; // Seconds
    private static readonly Queue<Node> s_queue = [];  // Queue for the await task
    internal static event Func<List<string>, Task>? s_eventViolated;

    static Donate25()
    {
        Coc.s_eventMemberJoined += MemberAdded;
        Coc.s_eventMemberLeft += MemberLeft;
        Coc.s_eventDonationFolded += DonationChanged;
        Dc.s_eventBotReady += BotReadyAsync;
        Member.s_eventAltAdded += AltAdded;
        Init();
    }

    private static void Init()
    {
        IEnumerable<IGrouping<long, Donation>> donationGroups = Db.GetDonations()
            .GroupBy(d => d.Checked)
            .OrderBy(g => g.Key);

        // Init donate25 vars
        foreach (IGrouping<long, Donation> group in donationGroups)
        {
            DateTimeOffset lastChecked = DateTimeOffset.FromUnixTimeSeconds(group.Key);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan timePassed = now - lastChecked;

            // If bot was down when a check is due, we will be lenient and wait for another cycle
            DateTimeOffset startingInstant = timePassed.TotalSeconds >= CheckPeriod ? now : lastChecked;
            long targetTime = startingInstant.ToUnixTimeSeconds() + CheckPeriod;

            Node node = new(targetTime);
            foreach (Donation donation in group)
            {
                node._ids.Add(donation.MainId);
            }
            s_queue.Enqueue(node);
        }

        Console.WriteLine("[Donate25] Inited");
    }

    private static async Task BotReadyAsync()
    {
        try
        {
            await CheckQueueAsync();
        }
        catch (Exception ex)
        {
            await Dc.ExceptionAsync(ex);
        }
    }

    private static async Task CheckQueueAsync()
    {
        while (s_queue.Count > 0)
        {
            Node node = s_queue.First();
            if (node._ids.Count == 0)
            {
                s_queue.Dequeue();
                continue;
            }

            int waitDelay = (int)((node._checkTime * 1000) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Task.Delay(waitDelay);

            node = s_queue.Dequeue();
            node._checkTime += CheckPeriod;
            List<string> violators = [];
            foreach (string member in node._ids)
            {
                IEnumerable<Alt> alts = new Member(member).GetAltsByMain();
                int altCount = alts.Count();
                int donationTarget = TargetPerPerson * (altCount + 1);
                Donation donation = Db.GetDonation(member)!;
                if (donation.Donated >= donationTarget)
                {
                    Console.WriteLine($"[Donate25] {member} new cycle");
                }
                else
                {
                    violators.Add(member);
                    Console.WriteLine($"[Donate25] {member} violated");
                }
                donation.Donated = 0;
                donation.Checked = node._checkTime;
                donation.Update();
            }

            if (node._ids.Count > 0)
            {
                s_queue.Enqueue(node);
            }

            if (violators.Count > 0)
            {
                await s_eventViolated!(violators);
            }
        }
    }

    private static Task DonationChanged(Dictionary<string, DonationTuple> foldedDelta)
    {
        foreach ((string tag, DonationTuple dt) in foldedDelta)
        {
            int donated = dt._donated;
            int received = dt._received;

            if (donated > received)
            {
                donated -= received;
                Donation donation = Db.GetDonation(tag)!;
                donation.Donated += (uint)donated;
                Console.WriteLine($"[Donate25] {tag} {donated}");
                Db.UpdateDonation(donation);
            }
        }
        return Task.CompletedTask;
    }

    private static void AltAdded(Alt alt)
    {
        string altId = alt.AltId, mainId = alt.MainId;
        Console.WriteLine($"[Donate25] Removing {altId} -> {mainId} (addalt)");
        Node? node = s_queue.FirstOrDefault(n => n._ids.Remove(altId));
        if (node != null)
        {
            Console.WriteLine($"[Donate25] Removed {altId} in {node._checkTime}");
            node._ids.Add(mainId);
            Donation altDon = Db.GetDonation(altId)!;
            Donation mainDon = Db.GetDonation(mainId)!;
            altDon.Delete();
            mainDon.Donated += altDon.Donated;
            mainDon.Update();
            Console.WriteLine($"[Donate25] Added {mainId} because it replaced {altId} as main");
        }
    }

    private static void MemberAdded(ClanMember member)
    {
        string id = member.Tag;
        Console.WriteLine($"[Donate25] Adding {id}");
        long targetTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + CheckPeriod;
        Node node = s_queue.Last();  // We expect at least 1 member in the db
        if (targetTime == node._checkTime)
        {
            node._ids.Add(id);
            Console.WriteLine($"[Donate25] Added {id} in {node._checkTime} (last node)");
        }
        else if (targetTime > node._checkTime)
        {
            node = new(targetTime);
            node._ids.Add(id);
            s_queue.Enqueue(node);
            Console.WriteLine($"[Donate25] Added {id} in {node._checkTime} (new node). New queue len: {s_queue.Count}");
        }
        else
        {
            throw new InvalidOperationException($"New member targetTime < last node check time. targetTime: {targetTime} Last node checktime: {node._checkTime}");
        }
    }

    private static void MemberLeft(ClanMember member, string? newMainId)
    {
        string id = member.Tag;
        Console.WriteLine($"[Donate25] Removing {id} -> {newMainId}");
        Node? node = s_queue.FirstOrDefault(n => n._ids.Remove(id));
        if (node != null)
        {
            Console.WriteLine($"[Donate25] Removed {id} in {node._checkTime}");
            if (newMainId != null)
            {
                node._ids.Add(newMainId);
                Donation donation = Db.GetDonation(id)!;
                donation.Delete();
                donation.MainId = newMainId;
                donation.Insert();
                Console.WriteLine($"[Donate25] Added {newMainId} because it replaced {id} as main");
            }
        }
    }
}
