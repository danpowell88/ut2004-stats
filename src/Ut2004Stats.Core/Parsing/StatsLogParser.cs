using System.Globalization;
using System.Text;

namespace Ut2004Stats.Core.Parsing;

/// <summary>
/// Reads a UT2004 built-in stats log (<c>bLocalLog=True</c> under
/// <c>[Engine.GameStats]</c>) into a <see cref="ParsedMatch"/>.
/// </summary>
/// <remarks>
/// Format notes that drive the implementation:
/// <list type="bullet">
/// <item>Lines are tab-delimited; field 0 is a float timestamp in seconds, field 1 is the tag.</item>
/// <item>There is no quoting or escaping, so a tab inside a name would shift fields.</item>
/// <item>Everything before the <c>SG</c> (start game) line is warm-up and is discarded.</item>
/// <item>Killing sprees are <em>not</em> logged — they are derived from consecutive kills.
/// Multi-kills, by contrast, are logged explicitly as <c>P multikill_N</c>.</item>
/// <item>Stock logs cannot distinguish bots from humans; only modded loggers add that.</item>
/// </list>
/// </remarks>
public class StatsLogParser
{
    /// <summary>Only the first match in a file is read; a mid-file map change ends parsing.</summary>
    public ParsedMatch ParseFile(string path)
    {
        using var stream = File.OpenRead(path);
        var match = Parse(stream);
        match.LogFileName = Path.GetFileName(path);
        ApplyFileNameHints(match);
        return match;
    }

    public ParsedMatch Parse(Stream stream)
    {
        var match = new ParsedMatch();

        foreach (var line in ReadLines(stream))
        {
            if (line.Length == 0) continue;

            var fields = line.Split('\t');
            if (fields.Length <= 1) continue;

            for (var i = 0; i < fields.Length; i++)
                fields[i] = fields[i].Trim();

            if (!TryParseTime(fields[0], out var time)) continue;

            var tag = fields[1].ToUpperInvariant();

            // Once the game has ended, the rest of the file belongs to a later match.
            if (match.EndReason != ParsedEndReason.None && tag != "EG") continue;

            switch (tag)
            {
                case "NG": HandleNewGame(match, fields); break;
                case "SI": HandleServerInfo(match, fields); break;
                case "SG": HandleStartGame(match, time); break;
                case "EG": HandleEndGame(match, time, fields); break;
                case "C": HandleConnect(match, time, fields); break;
                case "D": HandleDisconnect(match, time, fields); break;
                case "G": HandleGameEvent(match, time, fields); break;
                case "P": HandlePlayerEvent(match, time, fields); break;
                case "K": HandleKill(match, time, fields, teamKill: false); break;
                case "TK": HandleKill(match, time, fields, teamKill: true); break;
                case "S": HandleScore(match, fields); break;
                case "T": HandleTeamScore(match, fields); break;
                // MK/MD (Invasion monsters), I (pickups), PP/PS/PA/BI (metadata),
                // V/TV (chat) and the vote tags carry no data this app surfaces yet.
            }
        }

        Finalise(match);
        return match;
    }

    // ---- tag handlers -------------------------------------------------------

    private static void HandleNewGame(ParsedMatch m, string[] f)
    {
        if (f.Length < 10 || m.SawNewGame) return;

        m.SawNewGame = true;
        if (DateTime.TryParse(f[2], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var date))
            m.MatchDate = date;

        m.MapName = f[4];
        // "Untitled" is the engine's placeholder; fall back to the file name.
        m.MapTitle = string.IsNullOrWhiteSpace(f[5]) || f[5].Equals("Untitled", StringComparison.OrdinalIgnoreCase)
            ? f[4]
            : f[5];
        m.GameTypeClass = f[7];
        m.GameTypeName = f[8];
        m.Mutators = ParseMutators(f[9]);
    }

    /// <summary>Mutators arrive as a pipe-separated list of <c>Package.Class</c> names.</summary>
    private static string ParseMutators(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var names = raw
            .Split(['|', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(entry =>
            {
                var cls = GameCatalog.ShortName(entry.Trim());
                // Drop the conventional "Mut" prefix: MutInstaGib -> InstaGib.
                return cls.StartsWith("Mut", StringComparison.OrdinalIgnoreCase) && cls.Length > 3
                    ? cls[3..]
                    : cls;
            })
            .Where(n => n.Length > 0);

        return string.Join(", ", names);
    }

    private static void HandleServerInfo(ParsedMatch m, string[] f)
    {
        // The reference parser requires exactly 8 fields; be lenient but keep the
        // same field positions, since a short line means the blob is missing.
        if (f.Length < 8) return;

        m.ServerName = StripColourCodes(f[2]);

        foreach (var (key, value) in ParseServerBlob(f[7]))
        {
            switch (key)
            {
                case "timelimit":
                case "time limit":
                    if (int.TryParse(value, out var tl)) m.TimeLimit = tl;
                    break;
                case "goalscore":
                    if (int.TryParse(value, out var gs)) m.GoalScore = gs;
                    break;
            }
        }
    }

    /// <summary>Server info is a <c>\key\value\key\value</c> blob.</summary>
    private static IEnumerable<(string Key, string Value)> ParseServerBlob(string blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) yield break;

        var parts = blob.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i += 2)
            yield return (parts[i].Trim().ToLowerInvariant(), parts[i + 1].Trim());
    }

    /// <summary>
    /// Start of play. Everything logged before this point is warm-up, so all
    /// accumulated scoring is reset — connections themselves are kept.
    /// </summary>
    private static void HandleStartGame(ParsedMatch m, double time)
    {
        m.SawStartGame = true;
        m.StartTime = time;
        m.EndTime = time;

        Array.Clear(m.TeamScores);
        m.Kills.Clear();
        m.Specials.Clear();

        foreach (var p in m.Players.Values)
        {
            p.Frags = p.Deaths = p.Suicides = p.TeamKills = 0;
            p.Score = 0;
            p.FlagsTaken = p.FlagsCaptured = p.FlagsReturned = p.FlagsDropped = 0;
            p.FirstBlood = false;
            p.Headshots = 0;
            p.BestMultiKill = p.BestSpree = 0;
            p.CurrentSpreeKills = 0;
            p.ConnectTime = time;
        }
    }

    private static void HandleEndGame(ParsedMatch m, double time, string[] f)
    {
        if (m.EndReason != ParsedEndReason.None) return;
        if (f.Length < 3) return;

        m.EndTime = time;
        m.EndReason = f[2].ToLowerInvariant() switch
        {
            "fraglimit" => ParsedEndReason.FragLimit,
            "timelimit" => ParsedEndReason.TimeLimit,
            "teamscorelimit" => ParsedEndReason.TeamScoreLimit,
            "goalscorelimit" => ParsedEndReason.GoalScoreLimit,
            "roundlimit" => ParsedEndReason.RoundLimit,
            "lastman" => ParsedEndReason.LastMan,
            "draw" => ParsedEndReason.Draw,
            "artifacts" => ParsedEndReason.Artifacts,
            "mapchange" => ParsedEndReason.MapChange,
            "serverquit" => ParsedEndReason.ServerQuit,
            "endwarmup" => ParsedEndReason.EndWarmup,
            _ => ParsedEndReason.Unknown,
        };
    }

    private static void HandleConnect(ParsedMatch m, double time, string[] f)
    {
        if (f.Length < 4) return;
        if (!int.TryParse(f[2], out var slot) || slot < 0) return;

        var player = GetOrAdd(m, slot);
        player.ConnectTime = time;
        player.DisconnectTime = -1;

        // The field count selects the layout. Stock UT2004 emits exactly 4 fields.
        if (f.Length == 4)
        {
            player.Name = StripColourCodes(f[3]);
        }
        else if (f.Length == 5)
        {
            // id, team, name — only modded loggers emit this, and always for bots.
            if (int.TryParse(f[3], out var team)) player.Team = NormaliseTeam(team);
            player.Name = StripColourCodes(f[4]);
            player.IsBot = true;
        }
        else if (f.Length >= 7)
        {
            if (int.TryParse(f[3], out var team)) player.Team = NormaliseTeam(team);
            player.Name = StripColourCodes(f[4]);
        }
        else
        {
            // 6 fields is an identity line (cd-key/login hashes), no name to take.
            return;
        }

        StripBotPrefix(player);
    }

    private static void HandleDisconnect(ParsedMatch m, double time, string[] f)
    {
        if (f.Length < 3) return;
        if (!int.TryParse(f[2], out var slot) || slot < 0) return;
        if (!m.Players.TryGetValue(slot, out var player)) return;

        player.DisconnectTime = time;
        if (m.SawStartGame)
            player.TimePlayed += Math.Max(0, time - Math.Max(player.ConnectTime, m.StartTime));

        EndSpree(m, player, time);
    }

    private static void HandleGameEvent(ParsedMatch m, double time, string[] f)
    {
        if (f.Length < 3) return;

        var evt = f[2].ToLowerInvariant();
        var player = f.Length > 3 && int.TryParse(f[3], out var slot) && slot >= 0
            ? m.Players.GetValueOrDefault(slot)
            : null;

        switch (evt)
        {
            case "namechange" when f.Length >= 5 && player is not null:
                player.Name = StripColourCodes(f[4]);
                StripBotPrefix(player);
                break;

            case "teamchange" when f.Length >= 5 && player is not null:
                if (int.TryParse(f[4], out var team)) player.Team = NormaliseTeam(team);
                break;

            case "flag_taken" or "bomb_taken":
                if (player is not null) player.FlagsTaken++;
                break;

            case "flag_pickup" or "bomb_pickup":
                if (player is not null) player.FlagsTaken++;
                break;

            case "flag_dropped" or "bomb_dropped":
                if (player is not null) player.FlagsDropped++;
                break;

            case "flag_returned":
                if (player is not null) player.FlagsReturned++;
                break;

            case "flag_captured":
                if (player is not null) player.FlagsCaptured++;
                break;
        }
    }

    private static void HandlePlayerEvent(ParsedMatch m, double time, string[] f)
    {
        if (f.Length < 4 || !m.SawStartGame) return;
        if (!int.TryParse(f[2], out var slot) || slot < 0) return;
        if (!m.Players.TryGetValue(slot, out var player)) return;

        var evt = f[3].ToLowerInvariant();

        if (evt == "first_blood")
        {
            player.FirstBlood = true;
            m.Specials.Add(new ParsedSpecial
            {
                Time = time, SlotId = slot, Kind = ParsedSpecialKind.FirstBlood, Level = 1,
            });
            return;
        }

        // Multi-kills are logged cumulatively (a triple fires _1 then _2); keep the peak.
        if (evt.StartsWith("multikill_", StringComparison.Ordinal)
            && int.TryParse(evt.AsSpan("multikill_".Length), out var level)
            && level is >= 1 and <= 7)
        {
            if (level > player.BestMultiKill) player.BestMultiKill = level;
            m.Specials.Add(new ParsedSpecial
            {
                Time = time, SlotId = slot, Kind = ParsedSpecialKind.MultiKill, Level = level,
            });
        }

        // spree_N lines are deliberately ignored: sprees are derived from kill runs
        // so that the tier reflects the actual streak length.
    }

    private static void HandleKill(ParsedMatch m, double time, string[] f, bool teamKill)
    {
        if (f.Length < 6 || !m.SawStartGame) return;

        if (!int.TryParse(f[2], out var killerSlot)) return;
        if (!int.TryParse(f[4], out var victimSlot)) return;

        var damageType = f[3];

        // A victim we never saw connect means a corrupt or truncated line.
        if (victimSlot < 0 || killerSlot == PlayerSentinel.Unlogged) return;
        if (!m.Players.TryGetValue(victimSlot, out var victim)) return;

        var killer = killerSlot >= 0 ? m.Players.GetValueOrDefault(killerSlot) : null;

        if (teamKill)
        {
            // Team kills need both parties and count separately from frags.
            if (killer is null) return;

            killer.TeamKills++;
            m.Kills.Add(new ParsedKill
            {
                Time = time, KillerSlot = killerSlot, VictimSlot = victimSlot,
                DamageType = damageType, IsTeamKill = true,
            });
            EndSpree(m, victim, time);
            return;
        }

        var isSuicide = killerSlot == victimSlot
                        || (killerSlot < 0 && killerSlot > PlayerSentinel.Unlogged);

        if (isSuicide)
        {
            victim.Suicides++;
        }
        else if (killer is not null)
        {
            killer.Frags++;
            victim.Deaths++;

            killer.WeaponKills[damageType] = killer.WeaponKills.GetValueOrDefault(damageType) + 1;
            victim.WeaponDeaths[damageType] = victim.WeaponDeaths.GetValueOrDefault(damageType) + 1;

            // Headshots have no dedicated tag; the damage type is the only signal.
            if (damageType.Contains("HeadShot", StringComparison.OrdinalIgnoreCase))
                killer.Headshots++;

            killer.CurrentSpreeKills++;
        }
        else
        {
            victim.Deaths++;
        }

        m.Kills.Add(new ParsedKill
        {
            Time = time, KillerSlot = killerSlot, VictimSlot = victimSlot,
            DamageType = damageType, IsSuicide = isSuicide,
        });

        EndSpree(m, victim, time);
    }

    private static void HandleScore(ParsedMatch m, string[] f)
    {
        if (f.Length < 5 || !m.SawStartGame) return;
        if (!int.TryParse(f[2], out var slot) || slot < 0) return;
        if (!m.Players.TryGetValue(slot, out var player)) return;
        if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta)) return;

        // UT2004 under-awards critical frags; the reference parser pins them to 2.
        if (f[4] == "critical_frag") delta = 2.0;

        player.Score += delta;
    }

    private static void HandleTeamScore(ParsedMatch m, string[] f)
    {
        if (f.Length < 5 || !m.SawStartGame) return;
        if (!int.TryParse(f[2], out var team) || team is < 0 or > 3) return;
        if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta)) return;

        m.TeamScores[team] += delta;
    }

    // ---- helpers ------------------------------------------------------------

    private static ParsedPlayer GetOrAdd(ParsedMatch m, int slot)
    {
        if (m.Players.TryGetValue(slot, out var existing)) return existing;

        var player = new ParsedPlayer { SlotId = slot, Name = $"Player {slot}" };
        m.Players[slot] = player;
        return player;
    }

    /// <summary>255 means "no team"; anything outside 0-3 is treated the same way.</summary>
    private static int NormaliseTeam(int team) => team is >= 0 and <= 3 ? team : 255;

    /// <summary>Modded loggers mark bots with a literal <c>[BOT]</c> name prefix.</summary>
    private static void StripBotPrefix(ParsedPlayer player)
    {
        const string prefix = "[BOT]";
        if (!player.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        player.Name = player.Name[prefix.Length..].Trim();
        player.IsBot = true;
    }

    /// <summary>
    /// Closes a kill streak and records the tier reached. Buckets match the in-game
    /// announcements: 5 = Killing Spree, 10 = Rampage, 15 = Dominating,
    /// 20 = Unstoppable, 25 = Godlike, 30+ = Wicked Sick.
    /// </summary>
    private static void EndSpree(ParsedMatch m, ParsedPlayer player, double time)
    {
        var kills = player.CurrentSpreeKills;
        player.CurrentSpreeKills = 0;
        if (kills < 5) return;

        var level = kills switch
        {
            < 10 => 1,
            < 15 => 2,
            < 20 => 3,
            < 25 => 4,
            < 30 => 5,
            _ => 6,
        };

        if (level > player.BestSpree) player.BestSpree = level;
        m.Specials.Add(new ParsedSpecial
        {
            Time = time, SlotId = player.SlotId, Kind = ParsedSpecialKind.KillingSpree, Level = level,
        });
    }

    private static void Finalise(ParsedMatch m)
    {
        foreach (var player in m.Players.Values)
        {
            // Streaks still running when the match ended still count.
            EndSpree(m, player, m.EndTime);

            if (!m.SawStartGame) continue;

            // Players who never disconnected played through to the final whistle.
            if (player.DisconnectTime < 0)
                player.TimePlayed += Math.Max(0, m.EndTime - Math.Max(player.ConnectTime, m.StartTime));
        }
    }

    /// <summary>
    /// Recovers the port and start time from the conventional file name shape
    /// <c>Stats_&lt;port&gt;_yyyy_MM_dd_HH_mm_ss.log</c>, which is more reliable than
    /// the in-log date when the server clock and log clock disagree.
    /// </summary>
    private static void ApplyFileNameHints(ParsedMatch m)
    {
        var name = Path.GetFileNameWithoutExtension(m.LogFileName);
        var parts = name.Split('_');
        if (parts.Length < 8) return;

        if (int.TryParse(parts[1], out var port)) m.ServerPort = port;

        if (m.MatchDate != default) return;

        var stamp = string.Join('_', parts[2..8]);
        if (DateTime.TryParseExact(stamp, "yyyy_MM_dd_HH_mm_ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
            m.MatchDate = date;
    }

    private static bool TryParseTime(string value, out double time) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out time);

    /// <summary>UT colour codes are an ESC byte followed by three bytes.</summary>
    internal static string StripColourCodes(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\x1B')) return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\x1B') { i += 3; continue; }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads lines, detecting UTF-16LE logs. The engine writes either ANSI or
    /// UTF-16LE depending on platform, and the two are told apart by NUL bytes
    /// in the odd positions of the first line.
    /// </summary>
    private static IEnumerable<string> ReadLines(Stream stream)
    {
        var encoding = DetectEncoding(stream);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static Encoding DetectEncoding(Stream stream)
    {
        if (!stream.CanSeek) return Encoding.UTF8;

        Span<byte> probe = stackalloc byte[8];
        var read = stream.Read(probe);
        stream.Seek(0, SeekOrigin.Begin);

        if (read >= 6 && probe[1] == 0 && probe[3] == 0 && probe[5] == 0)
            return Encoding.Unicode;

        return Encoding.UTF8;
    }
}
