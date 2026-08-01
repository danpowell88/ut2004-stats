using System.Text;
using Ut2004Stats.Core.Parsing;

namespace Ut2004Stats.Core.Tests;

public class StatsLogParserTests
{
    private static ParsedMatch Parse(string log)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(log));
        return new StatsLogParser().Parse(stream);
    }

    /// <summary>Builds a log body wrapped in the mandatory NG/SI/SG ... EG scaffolding.</summary>
    private static string Log(string body, string endReason = "timelimit") =>
        string.Join('\n',
        [
            "0.00\tNG\t2026-08-01 17:19:04\tAUS\tDM-Rankin\tRankin\tCliffyB\tXGame.xDeathMatch\tDeathmatch\tXGame.MutInstaGib|UnrealGame.MutLowGrav",
            "0.00\tSI\tTech Bros\tAUS\tadmin\tadmin@example.com\t0\t\\serverversion\\3369\\timelimit\\20\\goalscore\\25\\",
            "1.00\tC\t0\tLoque",
            "1.50\tC\t1\tGorge",
            "2.00\tC\t2\tRiker",
            "5.00\tSG",
            body,
            $"600.00\tEG\t{endReason}",
        ]);

    [Fact]
    public void Parses_match_metadata()
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher"));

        Assert.True(m.IsComplete);
        Assert.Equal("DM-Rankin", m.MapName);
        Assert.Equal("Deathmatch", m.GameTypeName);
        Assert.Equal("XGame.xDeathMatch", m.GameTypeClass);
        Assert.Equal("Tech Bros", m.ServerName);
        Assert.Equal(20, m.TimeLimit);
        Assert.Equal(25, m.GoalScore);
        Assert.Equal(ParsedEndReason.TimeLimit, m.EndReason);
        Assert.Equal(new DateTime(2026, 8, 1, 17, 19, 4), m.MatchDate);
    }

    [Fact]
    public void Strips_mut_prefix_from_mutators()
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher"));
        Assert.Equal("InstaGib, LowGrav", m.Mutators);
    }

    [Fact]
    public void Duration_is_measured_from_start_game_not_file_start()
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher"));

        // SG at 5.00, EG at 600.00 — the 5s of warm-up must not be counted.
        Assert.Equal(595.0, m.DurationSeconds, precision: 2);
    }

    [Fact]
    public void Counts_frags_and_deaths()
    {
        var m = Parse(Log(string.Join('\n',
        [
            "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "12.00\tK\t0\tDamTypeFlakChunk\t1\tFlakCannon",
            "14.00\tK\t1\tDamTypeShockBeam\t0\tShockRifle",
        ])));

        Assert.Equal(2, m.Players[0].Frags);
        Assert.Equal(1, m.Players[0].Deaths);
        Assert.Equal(1, m.Players[1].Frags);
        Assert.Equal(2, m.Players[1].Deaths);
    }

    [Fact]
    public void Self_kill_is_a_suicide_and_not_a_frag()
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t0\tRocketLauncher"));

        Assert.Equal(1, m.Players[0].Suicides);
        Assert.Equal(0, m.Players[0].Frags);
        Assert.Equal(0, m.Players[0].Deaths);
    }

    [Fact]
    public void Environmental_death_with_negative_killer_is_a_suicide()
    {
        // A negative killer slot means the world killed them (fell, drowned...).
        var m = Parse(Log("10.00\tK\t-1\tfell\t0\tShieldGun"));

        Assert.Equal(1, m.Players[0].Suicides);
        Assert.Equal(0, m.Players[0].Deaths);
    }

    [Fact]
    public void Team_kills_are_tracked_separately_from_frags()
    {
        var m = Parse(Log("10.00\tTK\t0\tDamTypeRocket\t1\tRocketLauncher"));

        Assert.Equal(1, m.Players[0].TeamKills);
        Assert.Equal(0, m.Players[0].Frags);
        Assert.Equal(0, m.Players[1].Deaths);
    }

    [Fact]
    public void Ignores_kills_referencing_a_slot_that_never_connected()
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t99\tRocketLauncher"));

        Assert.Empty(m.Kills);
        Assert.Equal(0, m.Players[0].Frags);
    }

    [Fact]
    public void Warmup_activity_before_start_game_is_discarded()
    {
        // Two kills before SG, one after: only the post-SG kill may count.
        var log = string.Join('\n',
        [
            "0.00\tNG\t2026-08-01 17:19:04\tAUS\tDM-Rankin\tRankin\tCliffyB\tXGame.xDeathMatch\tDeathmatch\t",
            "0.00\tSI\tTech Bros\tAUS\tadmin\ta@b.c\t0\t\\timelimit\\20\\",
            "1.00\tC\t0\tLoque",
            "1.50\tC\t1\tGorge",
            "2.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "3.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "5.00\tSG",
            "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "600.00\tEG\ttimelimit",
        ]);

        var m = Parse(log);

        Assert.Equal(1, m.Players[0].Frags);
        Assert.Single(m.Kills);
    }

    [Theory]
    [InlineData(4, 0)]   // below the threshold — no spree
    [InlineData(5, 1)]   // Killing Spree
    [InlineData(10, 2)]  // Rampage
    [InlineData(15, 3)]  // Dominating
    [InlineData(20, 4)]  // Unstoppable
    [InlineData(25, 5)]  // Godlike
    [InlineData(30, 6)]  // Wicked Sick
    public void Derives_spree_tier_from_consecutive_kills(int kills, int expectedTier)
    {
        var lines = Enumerable.Range(0, kills)
            .Select(i => $"{10 + i}.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");

        var m = Parse(Log(string.Join('\n', lines)));

        Assert.Equal(expectedTier, m.Players[0].BestSpree);
    }

    [Fact]
    public void Dying_breaks_a_spree()
    {
        var lines = new List<string>();
        // Four kills, a death, then four more: neither run reaches five.
        for (var i = 0; i < 4; i++) lines.Add($"{10 + i}.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");
        lines.Add("20.00\tK\t1\tDamTypeShockBeam\t0\tShockRifle");
        for (var i = 0; i < 4; i++) lines.Add($"{30 + i}.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");

        var m = Parse(Log(string.Join('\n', lines)));

        Assert.Equal(0, m.Players[0].BestSpree);
    }

    [Fact]
    public void Keeps_highest_multikill_tier()
    {
        // Multi-kills escalate cumulatively; only the peak should be retained.
        var m = Parse(Log(string.Join('\n',
        [
            "10.00\tP\t0\tmultikill_1",
            "11.00\tP\t0\tmultikill_2",
            "12.00\tP\t0\tmultikill_3",
        ])));

        Assert.Equal(3, m.Players[0].BestMultiKill);
        Assert.Equal(3, m.Specials.Count(s => s.Kind == ParsedSpecialKind.MultiKill));
    }

    [Fact]
    public void Records_first_blood()
    {
        var m = Parse(Log("10.00\tP\t0\tfirst_blood"));

        Assert.True(m.Players[0].FirstBlood);
        Assert.Contains(m.Specials, s => s.Kind == ParsedSpecialKind.FirstBlood);
    }

    [Fact]
    public void Counts_headshots_from_damage_type()
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeSniperHeadShot\t1\tSniperRifle"));

        Assert.Equal(1, m.Players[0].Headshots);
    }

    [Fact]
    public void Applies_team_changes_and_team_scores()
    {
        var m = Parse(Log(string.Join('\n',
        [
            "6.00\tG\tteamchange\t0\t0",
            "6.50\tG\tteamchange\t1\t1",
            "10.00\tT\t0\t1.0\tflag_cap",
            "20.00\tT\t1\t2.0\tflag_cap",
        ])));

        Assert.Equal(0, m.Players[0].Team);
        Assert.Equal(1, m.Players[1].Team);
        Assert.Equal(1.0, m.TeamScores[0]);
        Assert.Equal(2.0, m.TeamScores[1]);
    }

    [Fact]
    public void Tracks_flag_events()
    {
        var m = Parse(Log(string.Join('\n',
        [
            "10.00\tG\tflag_taken\t0",
            "20.00\tG\tflag_captured\t0",
            "30.00\tG\tflag_returned\t1",
        ])));

        Assert.Equal(1, m.Players[0].FlagsTaken);
        Assert.Equal(1, m.Players[0].FlagsCaptured);
        Assert.Equal(1, m.Players[1].FlagsReturned);
    }

    [Fact]
    public void Overrides_critical_frag_score_to_two()
    {
        // The engine under-awards critical frags; the value is pinned to 2.
        var m = Parse(Log("10.00\tS\t0\t0.25\tcritical_frag"));

        Assert.Equal(2.0, m.Players[0].Score);
    }

    [Fact]
    public void Applies_name_changes()
    {
        var m = Parse(Log("10.00\tG\tnamechange\t0\tNewName"));
        Assert.Equal("NewName", m.Players[0].Name);
    }

    [Fact]
    public void Strips_colour_codes_from_names()
    {
        // A UT colour code is ESC followed by three bytes. Built explicitly from
        // char codes so the name's leading 'C' can't be absorbed by an escape.
        var colour = new string([(char)0x1B, (char)0x01, (char)0x02, (char)0x03]);
        var m = Parse(Log(string.Join('\t', ["10.00", "G", "namechange", "0", colour + "Colourful"])));
        Assert.Equal("Colourful", m.Players[0].Name);
    }

    [Fact]
    public void Detects_bot_prefix_on_modded_logs()
    {
        var m = Parse(Log("10.00\tG\tnamechange\t0\t[BOT]Xan"));

        Assert.True(m.Players[0].IsBot);
        Assert.Equal("Xan", m.Players[0].Name);
    }

    [Theory]
    [InlineData("fraglimit")]
    [InlineData("timelimit")]
    [InlineData("teamscorelimit")]
    [InlineData("goalscorelimit")]
    [InlineData("lastman")]
    public void Accepts_matches_that_reached_a_real_conclusion(string reason)
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher", reason));
        Assert.True(m.IsComplete);
    }

    [Theory]
    [InlineData("mapchange")]
    [InlineData("serverquit")]
    [InlineData("endwarmup")]
    public void Rejects_matches_that_did_not_finish(string reason)
    {
        var m = Parse(Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher", reason));
        Assert.False(m.IsComplete);
    }

    [Fact]
    public void Rejects_a_log_with_no_end_game_marker()
    {
        var log = string.Join('\n',
        [
            "0.00\tNG\t2026-08-01 17:19:04\tAUS\tDM-Rankin\tRankin\tCliffyB\tXGame.xDeathMatch\tDeathmatch\t",
            "0.00\tSI\tTech Bros\tAUS\tadmin\ta@b.c\t0\t\\timelimit\\20\\",
            "1.00\tC\t0\tLoque",
            "5.00\tSG",
            "10.00\tK\t0\tDamTypeRocket\t0\tRocketLauncher",
        ]);

        Assert.False(Parse(log).IsComplete);
    }

    [Fact]
    public void Rejects_a_log_that_never_started()
    {
        // NG and EG present but no SG: the match never actually got underway.
        var log = string.Join('\n',
        [
            "0.00\tNG\t2026-08-01 17:19:04\tAUS\tDM-Rankin\tRankin\tCliffyB\tXGame.xDeathMatch\tDeathmatch\t",
            "1.00\tC\t0\tLoque",
            "600.00\tEG\ttimelimit",
        ]);

        Assert.False(Parse(log).IsComplete);
    }

    [Fact]
    public void Only_reads_the_first_match_in_a_file()
    {
        // A map change can leave a second match in the same file; it must be ignored.
        var log = Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher")
                  + "\n601.00\tNG\t2026-08-01 18:00:00\tAUS\tCTF-Face\tFace\tEpic\tXGame.xCTFGame\tCapture the Flag\t"
                  + "\n610.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher";

        var m = Parse(log);

        Assert.Equal("DM-Rankin", m.MapName);
        Assert.Equal(1, m.Players[0].Frags);
    }

    [Fact]
    public void Reads_utf16_encoded_logs()
    {
        var text = Log("10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");
        using var stream = new MemoryStream(Encoding.Unicode.GetBytes(text));

        var m = new StatsLogParser().Parse(stream);

        Assert.True(m.IsComplete);
        Assert.Equal("DM-Rankin", m.MapName);
    }

    [Fact]
    public void Tolerates_a_truncated_line()
    {
        var m = Parse(Log(string.Join('\n',
        [
            "10.00\tK\t0\tDamTypeRocket",     // short kill line
            "11.00\tK",                        // tag only
            "12.00",                           // time only
            "13.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
        ])));

        Assert.Equal(1, m.Players[0].Frags);
    }
}

public class GameCatalogTests
{
    [Theory]
    [InlineData("XGame.xDeathMatch", "Deathmatch", false)]
    [InlineData("XGame.xCTFGame", "Capture the Flag", true)]
    [InlineData("Onslaught.ONSOnslaughtGame", "Onslaught", true)]
    [InlineData("XGame.xTeamGame", "Team Deathmatch", true)]
    public void Maps_known_gametypes(string cls, string expectedName, bool isTeam)
    {
        Assert.Equal(expectedName, GameCatalog.GameTypeName(cls));
        Assert.Equal(isTeam, GameCatalog.IsTeamGame(cls));
    }

    [Theory]
    [InlineData("DamTypeRocket", "Rocket Launcher")]
    [InlineData("RocketLauncher", "Rocket Launcher")]
    [InlineData("DamTypeFlakChunk", "Flak Cannon")]
    [InlineData("FlakCannon", "Flak Cannon")]
    [InlineData("DamTypeShockBeam", "Shock Rifle")]
    [InlineData("DamTypeSniperHeadShot", "Lightning Gun")]
    [InlineData("fell", "Falling")]
    public void Maps_weapons_and_damage_types(string raw, string expected)
        => Assert.Equal(expected, GameCatalog.WeaponName(raw));

    [Fact]
    public void Falls_back_to_a_readable_name_for_unknown_classes()
        => Assert.Equal("Some Mod Gun", GameCatalog.WeaponName("MyMod.SomeModGun"));

    [Theory]
    [InlineData("DM-Rankin", "DM")]
    [InlineData("CTF-Face", "CTF")]
    [InlineData("ONS-Torlan", "ONS")]
    public void Derives_map_prefix(string map, string expected)
        => Assert.Equal(expected, GameCatalog.MapPrefix(map));
}
