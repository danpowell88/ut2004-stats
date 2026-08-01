using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ut2004Stats.Core.Data;
using Ut2004Stats.Core.Domain;
using Ut2004Stats.Core.Parsing;

namespace Ut2004Stats.Core.Services;

public enum ImportOutcome
{
    Imported,
    /// <summary>The log was already in the database — imports are idempotent.</summary>
    AlreadyImported,
    /// <summary>The match never reached a real conclusion (warm-up, map change, crash).</summary>
    Incomplete,
    Failed,
}

public record ImportResult(string FileName, ImportOutcome Outcome, string? Detail = null);

/// <summary>
/// Turns parsed stat logs into database records. Safe to run repeatedly over the
/// same directory: a log file is only ever imported once.
/// </summary>
public class MatchImporter(StatsDbContext db, ILogger<MatchImporter> logger)
{
    private readonly StatsLogParser _parser = new();

    /// <summary>
    /// Imports every completed log in a directory. In-progress logs (<c>.log.tmp</c>)
    /// are skipped — the engine only finalises the name once the match ends.
    /// </summary>
    public async Task<IReadOnlyList<ImportResult>> ImportDirectoryAsync(
        string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogWarning("Stats directory {Directory} does not exist", directory);
            return [];
        }

        var results = new List<ImportResult>();

        // The extension is re-checked explicitly: Windows pattern matching can let
        // longer extensions (notably the in-progress ".log.tmp") slip through.
        var logs = Directory.EnumerateFiles(directory, "*.log")
            .Where(p => Path.GetExtension(p).Equals(".log", StringComparison.OrdinalIgnoreCase))
            .Order();

        foreach (var path in logs)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ImportFileAsync(path, ct));
        }

        var imported = results.Count(r => r.Outcome == ImportOutcome.Imported);
        if (imported > 0)
            logger.LogInformation("Imported {Count} new match(es) from {Directory}", imported, directory);

        return results;
    }

    public async Task<ImportResult> ImportFileAsync(string path, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(path);

        try
        {
            if (await db.Matches.AnyAsync(m => m.LogFileName == fileName, ct))
                return new ImportResult(fileName, ImportOutcome.AlreadyImported);

            var parsed = _parser.ParseFile(path);

            if (!parsed.IsComplete)
            {
                logger.LogDebug("Skipping {File}: {Reason}", fileName, DescribeIncomplete(parsed));
                return new ImportResult(fileName, ImportOutcome.Incomplete, DescribeIncomplete(parsed));
            }

            await SaveAsync(parsed, ct);
            logger.LogInformation("Imported {File}: {Map} ({Players} players)",
                fileName, parsed.MapName, parsed.Players.Count);

            return new ImportResult(fileName, ImportOutcome.Imported);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import {File}", fileName);
            return new ImportResult(fileName, ImportOutcome.Failed, ex.Message);
        }
    }

    private static string DescribeIncomplete(ParsedMatch m)
    {
        if (!m.SawNewGame) return "no game start record";
        if (!m.SawStartGame) return "match never started";
        if (m.Players.Count == 0) return "no players";
        return m.EndReason switch
        {
            ParsedEndReason.None => "match did not finish",
            ParsedEndReason.MapChange => "ended by map change",
            ParsedEndReason.ServerQuit => "ended by server shutdown",
            ParsedEndReason.EndWarmup => "warm-up round",
            _ => $"unusable end reason ({m.EndReason})",
        };
    }

    private async Task SaveAsync(ParsedMatch parsed, CancellationToken ct)
    {
        var server = await GetOrCreateServerAsync(parsed, ct);
        var map = await GetOrCreateMapAsync(parsed.MapName, ct);
        var gameType = await GetOrCreateGameTypeAsync(parsed, ct);

        var isTeamGame = gameType.IsTeamGame;

        var match = new Match
        {
            Server = server,
            Map = map,
            GameType = gameType,
            StartedAt = parsed.MatchDate,
            DurationSeconds = parsed.DurationSeconds,
            TimeLimit = parsed.TimeLimit,
            GoalScore = parsed.GoalScore,
            EndReason = MapEndReason(parsed.EndReason),
            RedScore = isTeamGame ? (int)Math.Round(parsed.TeamScores[Teams.Red]) : 0,
            BlueScore = isTeamGame ? (int)Math.Round(parsed.TeamScores[Teams.Blue]) : 0,
            Mutators = parsed.Mutators,
            LogFileName = parsed.LogFileName,
            ImportedAt = DateTime.UtcNow,
        };

        db.Matches.Add(match);

        // Slot id -> the MatchPlayer row, so kill events can be linked afterwards.
        var bySlot = new Dictionary<int, MatchPlayer>();

        foreach (var p in parsed.Players.Values.OrderBy(p => p.SlotId))
        {
            var player = await GetOrCreatePlayerAsync(p, parsed.MatchDate, ct);

            var matchPlayer = new MatchPlayer
            {
                Match = match,
                Player = player,
                SlotId = p.SlotId,
                NameUsed = p.Name,
                Team = isTeamGame ? p.Team : Teams.None,
                IsBot = p.IsBot,
                Score = (int)Math.Round(p.Score),
                Frags = p.Frags,
                Deaths = p.Deaths,
                Suicides = p.Suicides,
                TeamKills = p.TeamKills,
                TimePlayed = p.TimePlayed,
                FlagsTaken = p.FlagsTaken,
                FlagsCaptured = p.FlagsCaptured,
                FlagsReturned = p.FlagsReturned,
                FirstBloods = p.FirstBlood ? 1 : 0,
                Headshots = p.Headshots,
                BestSpree = p.BestSpree,
                BestMultiKill = p.BestMultiKill,
            };

            match.Players.Add(matchPlayer);
            bySlot[p.SlotId] = matchPlayer;

            await AddWeaponTotalsAsync(matchPlayer, p, ct);
        }

        foreach (var k in parsed.Kills)
        {
            if (!bySlot.TryGetValue(k.VictimSlot, out var victim)) continue;
            bySlot.TryGetValue(k.KillerSlot, out var killer);

            match.Kills.Add(new KillEvent
            {
                Match = match,
                Time = k.Time,
                KillerMatchPlayer = killer,
                VictimMatchPlayer = victim,
                Weapon = await GetOrCreateWeaponAsync(k.DamageType, ct),
                IsSuicide = k.IsSuicide,
                IsTeamKill = k.IsTeamKill,
            });
        }

        foreach (var s in parsed.Specials)
        {
            if (!bySlot.TryGetValue(s.SlotId, out var mp)) continue;

            match.SpecialEvents.Add(new SpecialEvent
            {
                Match = match,
                MatchPlayer = mp,
                Time = s.Time,
                Kind = s.Kind switch
                {
                    ParsedSpecialKind.MultiKill => SpecialEventKind.MultiKill,
                    ParsedSpecialKind.KillingSpree => SpecialEventKind.KillingSpree,
                    _ => SpecialEventKind.FirstBlood,
                },
                Level = s.Level,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task AddWeaponTotalsAsync(MatchPlayer mp, ParsedPlayer p, CancellationToken ct)
    {
        // Damage types collapse onto their weapon, so both fire modes count together.
        var kills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var deaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (damageType, count) in p.WeaponKills)
            kills[GameCatalog.WeaponName(damageType)] = kills.GetValueOrDefault(GameCatalog.WeaponName(damageType)) + count;

        foreach (var (damageType, count) in p.WeaponDeaths)
            deaths[GameCatalog.WeaponName(damageType)] = deaths.GetValueOrDefault(GameCatalog.WeaponName(damageType)) + count;

        foreach (var name in kills.Keys.Union(deaths.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mp.Weapons.Add(new MatchPlayerWeapon
            {
                MatchPlayer = mp,
                Weapon = await GetOrCreateWeaponByDisplayNameAsync(name, ct),
                Kills = kills.GetValueOrDefault(name),
                Deaths = deaths.GetValueOrDefault(name),
            });
        }
    }

    private static MatchEndReason MapEndReason(ParsedEndReason reason) => reason switch
    {
        ParsedEndReason.TimeLimit => MatchEndReason.TimeLimit,
        ParsedEndReason.FragLimit => MatchEndReason.FragLimit,
        ParsedEndReason.TeamScoreLimit or ParsedEndReason.GoalScoreLimit => MatchEndReason.ScoreLimit,
        ParsedEndReason.None => MatchEndReason.Unknown,
        _ => MatchEndReason.Other,
    };

    // ---- lookup-or-create helpers ------------------------------------------
    // Each checks the change tracker first so repeated hits within one import
    // reuse the pending entity instead of inserting a duplicate.

    private async Task<Server> GetOrCreateServerAsync(ParsedMatch parsed, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(parsed.ServerName) ? "Unknown Server" : parsed.ServerName;
        var port = parsed.ServerPort;

        var existing = db.Servers.Local.FirstOrDefault(s => s.Name == name && s.Port == port)
                       ?? await db.Servers.FirstOrDefaultAsync(s => s.Name == name && s.Port == port, ct);

        if (existing is not null)
        {
            if (parsed.MatchDate > existing.LastSeen) existing.LastSeen = parsed.MatchDate;
            if (parsed.MatchDate < existing.FirstSeen) existing.FirstSeen = parsed.MatchDate;
            return existing;
        }

        var server = new Server
        {
            Name = name,
            Port = port,
            FirstSeen = parsed.MatchDate,
            LastSeen = parsed.MatchDate,
        };
        db.Servers.Add(server);
        return server;
    }

    private async Task<GameMap> GetOrCreateMapAsync(string mapName, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(mapName) ? "Unknown" : mapName;

        var existing = db.Maps.Local.FirstOrDefault(m => m.Name == name)
                       ?? await db.Maps.FirstOrDefaultAsync(m => m.Name == name, ct);
        if (existing is not null) return existing;

        var map = new GameMap { Name = name, Prefix = GameCatalog.MapPrefix(name) };
        db.Maps.Add(map);
        return map;
    }

    private async Task<GameType> GetOrCreateGameTypeAsync(ParsedMatch parsed, CancellationToken ct)
    {
        // Prefer the class name: it is unambiguous, whereas the display name can be
        // localised or renamed by mods.
        var key = string.IsNullOrWhiteSpace(parsed.GameTypeClass)
            ? parsed.GameTypeName
            : parsed.GameTypeClass;

        if (string.IsNullOrWhiteSpace(key)) key = "Unknown";

        var existing = db.GameTypes.Local.FirstOrDefault(g => g.ClassName == key)
                       ?? await db.GameTypes.FirstOrDefaultAsync(g => g.ClassName == key, ct);
        if (existing is not null) return existing;

        // The logged display name is the better label when we have one.
        var display = !string.IsNullOrWhiteSpace(parsed.GameTypeName)
            ? GameCatalog.GameTypeName(parsed.GameTypeName)
            : GameCatalog.GameTypeName(key);

        var gameType = new GameType
        {
            ClassName = key,
            DisplayName = display,
            IsTeamGame = GameCatalog.IsTeamGame(key) || GameCatalog.IsTeamGame(parsed.GameTypeName),
        };
        db.GameTypes.Add(gameType);
        return gameType;
    }

    private async Task<Player> GetOrCreatePlayerAsync(ParsedPlayer p, DateTime seenAt, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(p.Name) ? $"Player {p.SlotId}" : p.Name;

        var existing = db.Players.Local.FirstOrDefault(x => x.Name == name)
                       ?? await db.Players.FirstOrDefaultAsync(x => x.Name == name, ct);

        if (existing is not null)
        {
            if (seenAt > existing.LastSeen) existing.LastSeen = seenAt;
            if (seenAt < existing.FirstSeen) existing.FirstSeen = seenAt;
            // Once a player is known to be a bot, keep that flag.
            existing.IsBot |= p.IsBot;
            return existing;
        }

        var player = new Player
        {
            Name = name,
            IsBot = p.IsBot,
            FirstSeen = seenAt,
            LastSeen = seenAt,
        };
        db.Players.Add(player);
        return player;
    }

    private Task<Weapon> GetOrCreateWeaponAsync(string damageType, CancellationToken ct) =>
        GetOrCreateWeaponByDisplayNameAsync(GameCatalog.WeaponName(damageType), ct);

    private async Task<Weapon> GetOrCreateWeaponByDisplayNameAsync(string displayName, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "Unknown" : displayName;

        var existing = db.Weapons.Local.FirstOrDefault(w => w.ClassName == name)
                       ?? await db.Weapons.FirstOrDefaultAsync(w => w.ClassName == name, ct);
        if (existing is not null) return existing;

        var weapon = new Weapon { ClassName = name, DisplayName = name };
        db.Weapons.Add(weapon);
        return weapon;
    }
}
