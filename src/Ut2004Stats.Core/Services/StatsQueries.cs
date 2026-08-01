using Microsoft.EntityFrameworkCore;
using Ut2004Stats.Core.Data;
using Ut2004Stats.Core.Domain;

namespace Ut2004Stats.Core.Services;

public record SiteSummary(
    int Matches,
    int Players,
    int Maps,
    long TotalFrags,
    double TotalHours,
    DateTime? LastMatch);

public record LeaderboardRow(
    int PlayerId,
    string Name,
    bool IsBot,
    int Matches,
    int Frags,
    int Deaths,
    int Score,
    double Efficiency,
    double Ratio,
    double FragsPerMinute,
    int BestSpree,
    int BestMultiKill,
    double HoursPlayed,
    DateTime LastSeen);

public record MatchSummary(
    int Id,
    DateTime StartedAt,
    string Map,
    string MapPrefix,
    string GameType,
    bool IsTeamGame,
    int RedScore,
    int BlueScore,
    int PlayerCount,
    double DurationMinutes,
    string TopPlayer,
    int TopScore);

public record PlayerProfile(
    int Id,
    string Name,
    bool IsBot,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Matches,
    int Frags,
    int Deaths,
    int Suicides,
    int TeamKills,
    int Headshots,
    int FirstBloods,
    int BestSpree,
    int BestMultiKill,
    double HoursPlayed,
    double Efficiency,
    double Ratio,
    double AvgScore,
    double FragsPerMinute);

public record NamedCount(string Name, int Count);
public record TrendPoint(DateTime Date, int Matches, int Frags);
public record WeaponUsage(string Weapon, int Kills, int Deaths, double SharePercent);
public record MapPopularity(string Map, string Prefix, int Matches, double AvgDurationMinutes);
public record HeadToHead(string Opponent, int KilledThem, int KilledBy);

public record MatchScoreboardRow(
    int MatchPlayerId,
    int PlayerId,
    string Name,
    int Team,
    bool IsBot,
    int Score,
    int Frags,
    int Deaths,
    int Suicides,
    double Efficiency,
    double Ratio,
    int BestSpree,
    int BestMultiKill,
    int Headshots,
    int FlagsCaptured,
    double MinutesPlayed);

public record MatchDetail(
    int Id,
    DateTime StartedAt,
    string Map,
    string MapPrefix,
    string GameType,
    bool IsTeamGame,
    string Server,
    string Mutators,
    int RedScore,
    int BlueScore,
    double DurationMinutes,
    MatchEndReason EndReason,
    IReadOnlyList<MatchScoreboardRow> Scoreboard,
    IReadOnlyList<WeaponUsage> Weapons);

/// <summary>Read-side aggregations for the site. All queries are read-only.</summary>
public class StatsQueries(StatsDbContext db)
{
    /// <summary>Bots are excluded from rankings by default so ladders reflect real players.</summary>
    private const bool ExcludeBotsByDefault = true;

    public async Task<SiteSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var matches = await db.Matches.CountAsync(ct);
        var players = await db.Players.CountAsync(ct);
        var maps = await db.Maps.CountAsync(ct);
        var frags = await db.MatchPlayers.SumAsync(p => (long?)p.Frags, ct) ?? 0;
        var seconds = await db.Matches.SumAsync(m => (double?)m.DurationSeconds, ct) ?? 0;
        var last = await db.Matches.OrderByDescending(m => m.StartedAt)
            .Select(m => (DateTime?)m.StartedAt).FirstOrDefaultAsync(ct);

        return new SiteSummary(matches, players, maps, frags, Math.Round(seconds / 3600, 1), last);
    }

    public async Task<IReadOnlyList<LeaderboardRow>> GetLeaderboardAsync(
        int take = 50, bool includeBots = !ExcludeBotsByDefault, CancellationToken ct = default)
    {
        var query = db.MatchPlayers.AsNoTracking();
        if (!includeBots) query = query.Where(mp => !mp.Player.IsBot);

        var rows = await query
            .GroupBy(mp => new { mp.PlayerId, mp.Player.Name, mp.Player.IsBot, mp.Player.LastSeen })
            .Select(g => new
            {
                g.Key.PlayerId,
                g.Key.Name,
                g.Key.IsBot,
                g.Key.LastSeen,
                Matches = g.Count(),
                Frags = g.Sum(x => x.Frags),
                Deaths = g.Sum(x => x.Deaths),
                Score = g.Sum(x => x.Score),
                Seconds = g.Sum(x => x.TimePlayed),
                BestSpree = g.Max(x => x.BestSpree),
                BestMulti = g.Max(x => x.BestMultiKill),
            })
            .OrderByDescending(x => x.Frags)
            .Take(take)
            .ToListAsync(ct);

        return [.. rows.Select(r => new LeaderboardRow(
            r.PlayerId,
            r.Name,
            r.IsBot,
            r.Matches,
            r.Frags,
            r.Deaths,
            r.Score,
            Percent(r.Frags, r.Frags + r.Deaths),
            Ratio(r.Frags, r.Deaths),
            PerMinute(r.Frags, r.Seconds),
            r.BestSpree,
            r.BestMulti,
            Math.Round(r.Seconds / 3600, 1),
            r.LastSeen))];
    }

    public async Task<IReadOnlyList<MatchSummary>> GetRecentMatchesAsync(
        int take = 20, CancellationToken ct = default)
    {
        var matches = await db.Matches.AsNoTracking()
            .Include(m => m.Map)
            .Include(m => m.GameType)
            .Include(m => m.Players).ThenInclude(p => p.Player)
            .OrderByDescending(m => m.StartedAt)
            .Take(take)
            .ToListAsync(ct);

        return [.. matches.Select(ToSummary)];
    }

    private static MatchSummary ToSummary(Match m)
    {
        var top = m.Players.OrderByDescending(p => p.Score).ThenByDescending(p => p.Frags).FirstOrDefault();

        return new MatchSummary(
            m.Id,
            m.StartedAt,
            m.Map.Name,
            m.Map.Prefix,
            m.GameType.DisplayName,
            m.GameType.IsTeamGame,
            m.RedScore,
            m.BlueScore,
            m.Players.Count,
            Math.Round(m.DurationSeconds / 60, 1),
            top?.NameUsed ?? "—",
            top?.Score ?? 0);
    }

    public async Task<MatchDetail?> GetMatchAsync(int id, CancellationToken ct = default)
    {
        var m = await db.Matches.AsNoTracking()
            .Include(x => x.Map)
            .Include(x => x.GameType)
            .Include(x => x.Server)
            .Include(x => x.Players).ThenInclude(p => p.Player)
            .Include(x => x.Players).ThenInclude(p => p.Weapons).ThenInclude(w => w.Weapon)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (m is null) return null;

        var scoreboard = m.Players
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.Frags)
            .Select(p => new MatchScoreboardRow(
                p.Id, p.PlayerId, p.NameUsed, p.Team, p.IsBot,
                p.Score, p.Frags, p.Deaths, p.Suicides,
                p.Efficiency, p.Ratio, p.BestSpree, p.BestMultiKill,
                p.Headshots, p.FlagsCaptured,
                Math.Round(p.TimePlayed / 60, 1)))
            .ToList();

        var weaponTotals = m.Players
            .SelectMany(p => p.Weapons)
            .GroupBy(w => w.Weapon.DisplayName)
            .Select(g => new { Weapon = g.Key, Kills = g.Sum(x => x.Kills), Deaths = g.Sum(x => x.Deaths) })
            .OrderByDescending(x => x.Kills)
            .ToList();

        var totalKills = weaponTotals.Sum(w => w.Kills);
        var weapons = weaponTotals
            .Select(w => new WeaponUsage(w.Weapon, w.Kills, w.Deaths, Percent(w.Kills, totalKills)))
            .ToList();

        return new MatchDetail(
            m.Id, m.StartedAt, m.Map.Name, m.Map.Prefix,
            m.GameType.DisplayName, m.GameType.IsTeamGame,
            m.Server.Name, m.Mutators,
            m.RedScore, m.BlueScore,
            Math.Round(m.DurationSeconds / 60, 1),
            m.EndReason, scoreboard, weapons);
    }

    public async Task<PlayerProfile?> GetPlayerAsync(int id, CancellationToken ct = default)
    {
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (player is null) return null;

        var agg = await db.MatchPlayers.AsNoTracking()
            .Where(mp => mp.PlayerId == id)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Matches = g.Count(),
                Frags = g.Sum(x => x.Frags),
                Deaths = g.Sum(x => x.Deaths),
                Suicides = g.Sum(x => x.Suicides),
                TeamKills = g.Sum(x => x.TeamKills),
                Headshots = g.Sum(x => x.Headshots),
                FirstBloods = g.Sum(x => x.FirstBloods),
                BestSpree = g.Max(x => x.BestSpree),
                BestMulti = g.Max(x => x.BestMultiKill),
                Score = g.Sum(x => x.Score),
                Seconds = g.Sum(x => x.TimePlayed),
            })
            .FirstOrDefaultAsync(ct);

        if (agg is null)
            return new PlayerProfile(player.Id, player.Name, player.IsBot, player.FirstSeen,
                player.LastSeen, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        return new PlayerProfile(
            player.Id, player.Name, player.IsBot, player.FirstSeen, player.LastSeen,
            agg.Matches, agg.Frags, agg.Deaths, agg.Suicides, agg.TeamKills,
            agg.Headshots, agg.FirstBloods, agg.BestSpree, agg.BestMulti,
            Math.Round(agg.Seconds / 3600, 1),
            Percent(agg.Frags, agg.Frags + agg.Deaths),
            Ratio(agg.Frags, agg.Deaths),
            agg.Matches == 0 ? 0 : Math.Round((double)agg.Score / agg.Matches, 1),
            PerMinute(agg.Frags, agg.Seconds));
    }

    public async Task<IReadOnlyList<MatchSummary>> GetPlayerMatchesAsync(
        int playerId, int take = 15, CancellationToken ct = default)
    {
        var matches = await db.Matches.AsNoTracking()
            .Where(m => m.Players.Any(p => p.PlayerId == playerId))
            .Include(m => m.Map)
            .Include(m => m.GameType)
            .Include(m => m.Players).ThenInclude(p => p.Player)
            .OrderByDescending(m => m.StartedAt)
            .Take(take)
            .ToListAsync(ct);

        return [.. matches.Select(ToSummary)];
    }

    /// <summary>Weapons a player kills most with, for their profile breakdown.</summary>
    public async Task<IReadOnlyList<WeaponUsage>> GetPlayerWeaponsAsync(
        int playerId, CancellationToken ct = default)
    {
        var rows = await db.MatchPlayerWeapons.AsNoTracking()
            .Where(w => w.MatchPlayer.PlayerId == playerId)
            .GroupBy(w => w.Weapon.DisplayName)
            .Select(g => new { Weapon = g.Key, Kills = g.Sum(x => x.Kills), Deaths = g.Sum(x => x.Deaths) })
            .OrderByDescending(x => x.Kills)
            .ToListAsync(ct);

        var total = rows.Sum(r => r.Kills);
        return [.. rows.Select(r => new WeaponUsage(r.Weapon, r.Kills, r.Deaths, Percent(r.Kills, total)))];
    }

    /// <summary>Who this player kills most, and who kills them most.</summary>
    public async Task<IReadOnlyList<HeadToHead>> GetHeadToHeadAsync(
        int playerId, int take = 8, CancellationToken ct = default)
    {
        var kills = await db.Kills.AsNoTracking()
            .Where(k => !k.IsSuicide && !k.IsTeamKill
                        && k.KillerMatchPlayer!.PlayerId == playerId)
            .GroupBy(k => k.VictimMatchPlayer.Player.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var deaths = await db.Kills.AsNoTracking()
            .Where(k => !k.IsSuicide && !k.IsTeamKill
                        && k.VictimMatchPlayer.PlayerId == playerId
                        && k.KillerMatchPlayerId != null)
            .GroupBy(k => k.KillerMatchPlayer!.Player.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var names = kills.Select(k => k.Name).Union(deaths.Select(d => d.Name));

        return [.. names
            .Select(n => new HeadToHead(
                n,
                kills.FirstOrDefault(k => k.Name == n)?.Count ?? 0,
                deaths.FirstOrDefault(d => d.Name == n)?.Count ?? 0))
            .OrderByDescending(h => h.KilledThem + h.KilledBy)
            .Take(take)];
    }

    public async Task<IReadOnlyList<MapPopularity>> GetMapPopularityAsync(
        int take = 10, CancellationToken ct = default)
    {
        var rows = await db.Matches.AsNoTracking()
            .GroupBy(m => new { m.Map.Name, m.Map.Prefix })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Prefix,
                Matches = g.Count(),
                AvgSeconds = g.Average(x => x.DurationSeconds),
            })
            .OrderByDescending(x => x.Matches)
            .Take(take)
            .ToListAsync(ct);

        return [.. rows.Select(r => new MapPopularity(
            r.Name, r.Prefix, r.Matches, Math.Round(r.AvgSeconds / 60, 1)))];
    }

    public async Task<IReadOnlyList<NamedCount>> GetGameTypeBreakdownAsync(CancellationToken ct = default)
    {
        // Projected to an anonymous type first: EF cannot translate a grouping
        // straight into a record constructor.
        var rows = await db.Matches.AsNoTracking()
            .GroupBy(m => m.GameType.DisplayName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        return [.. rows.Select(r => new NamedCount(r.Name, r.Count))];
    }

    /// <summary>Daily match/frag counts for the activity chart.</summary>
    public async Task<IReadOnlyList<TrendPoint>> GetActivityAsync(
        int days = 30, CancellationToken ct = default)
    {
        var since = DateTime.Now.Date.AddDays(-days + 1);

        var rows = await db.Matches.AsNoTracking()
            .Where(m => m.StartedAt >= since)
            .Select(m => new { m.StartedAt, Frags = m.Players.Sum(p => p.Frags) })
            .ToListAsync(ct);

        var byDay = rows
            .GroupBy(r => r.StartedAt.Date)
            .ToDictionary(g => g.Key, g => (Matches: g.Count(), Frags: g.Sum(x => x.Frags)));

        // Emit a point per day, including quiet ones, so the chart has no gaps.
        return [.. Enumerable.Range(0, days)
            .Select(i => since.AddDays(i))
            .Select(d => byDay.TryGetValue(d, out var v)
                ? new TrendPoint(d, v.Matches, v.Frags)
                : new TrendPoint(d, 0, 0))];
    }

    public async Task<IReadOnlyList<WeaponUsage>> GetWeaponUsageAsync(
        int take = 12, CancellationToken ct = default)
    {
        var rows = await db.MatchPlayerWeapons.AsNoTracking()
            .GroupBy(w => w.Weapon.DisplayName)
            .Select(g => new { Weapon = g.Key, Kills = g.Sum(x => x.Kills), Deaths = g.Sum(x => x.Deaths) })
            .OrderByDescending(x => x.Kills)
            .Take(take)
            .ToListAsync(ct);

        var total = rows.Sum(r => r.Kills);
        return [.. rows.Select(r => new WeaponUsage(r.Weapon, r.Kills, r.Deaths, Percent(r.Kills, total)))];
    }

    public async Task<IReadOnlyList<LeaderboardRow>> SearchPlayersAsync(
        string term, int take = 25, CancellationToken ct = default)
    {
        var all = await GetLeaderboardAsync(500, includeBots: true, ct);
        if (string.IsNullOrWhiteSpace(term)) return [.. all.Take(take)];

        return [.. all
            .Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(take)];
    }

    private static double Percent(int part, int whole) =>
        whole == 0 ? 0 : Math.Round(100.0 * part / whole, 1);

    private static double Ratio(int frags, int deaths) =>
        deaths == 0 ? frags : Math.Round((double)frags / deaths, 2);

    private static double PerMinute(int frags, double seconds) =>
        seconds < 1 ? 0 : Math.Round(frags / (seconds / 60), 2);
}
