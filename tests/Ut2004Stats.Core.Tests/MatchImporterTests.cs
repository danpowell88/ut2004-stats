using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ut2004Stats.Core.Data;
using Ut2004Stats.Core.Domain;
using Ut2004Stats.Core.Services;

namespace Ut2004Stats.Core.Tests;

public class MatchImporterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<StatsDbContext> _options;
    private readonly string _dir;

    public MatchImporterTests()
    {
        // A shared in-memory SQLite connection gives real relational behaviour
        // (constraints, cascades) without touching disk.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<StatsDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new StatsDbContext(_options);
        db.Database.EnsureCreated();

        _dir = Path.Combine(Path.GetTempPath(), "ut2004stats-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private StatsDbContext NewContext() => new(_options);

    private MatchImporter NewImporter(StatsDbContext db) =>
        new(db, NullLogger<MatchImporter>.Instance);

    private string WriteLog(string fileName, params string[] bodyLines)
    {
        var lines = new List<string>
        {
            "0.00\tNG\t2026-08-01 17:19:04\tAUS\tCTF-Face\tFace\tEpic\tXGame.xCTFGame\tCapture the Flag\t",
            "0.00\tSI\tTech Bros\tAUS\tadmin\ta@b.c\t0\t\\timelimit\\20\\goalscore\\3\\",
            "1.00\tC\t0\tLoque",
            "1.50\tC\t1\tGorge",
            "5.00\tSG",
            "6.00\tG\tteamchange\t0\t0",
            "6.50\tG\tteamchange\t1\t1",
        };
        lines.AddRange(bodyLines);
        lines.Add("600.00\tEG\ttimelimit");

        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, string.Join('\n', lines));
        return path;
    }

    [Fact]
    public async Task Imports_a_completed_match()
    {
        var path = WriteLog("Stats_7777_2026_08_01_17_19_04.log",
            "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "12.00\tK\t0\tDamTypeFlakChunk\t1\tFlakCannon",
            "14.00\tK\t1\tDamTypeShockBeam\t0\tShockRifle",
            "20.00\tT\t0\t1.0\tflag_cap");

        await using var db = NewContext();
        var result = await NewImporter(db).ImportFileAsync(path);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);

        var match = await db.Matches
            .Include(m => m.Map)
            .Include(m => m.GameType)
            .Include(m => m.Players)
            .SingleAsync();

        Assert.Equal("CTF-Face", match.Map.Name);
        Assert.Equal("CTF", match.Map.Prefix);
        Assert.True(match.GameType.IsTeamGame);
        Assert.Equal(2, match.Players.Count);
        Assert.Equal(1, match.RedScore);

        var loque = match.Players.Single(p => p.NameUsed == "Loque");
        Assert.Equal(2, loque.Frags);
        Assert.Equal(1, loque.Deaths);
        Assert.Equal(Teams.Red, loque.Team);
    }

    [Fact]
    public async Task Importing_the_same_log_twice_creates_one_match()
    {
        var path = WriteLog("Stats_7777_2026_08_01_17_19_04.log",
            "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");

        await using (var db = NewContext())
            Assert.Equal(ImportOutcome.Imported, (await NewImporter(db).ImportFileAsync(path)).Outcome);

        await using (var db = NewContext())
            Assert.Equal(ImportOutcome.AlreadyImported, (await NewImporter(db).ImportFileAsync(path)).Outcome);

        await using (var verify = NewContext())
            Assert.Equal(1, await verify.Matches.CountAsync());
    }

    [Fact]
    public async Task Skips_a_match_that_never_finished()
    {
        var path = Path.Combine(_dir, "Stats_7777_2026_08_01_18_00_00.log");
        File.WriteAllText(path, string.Join('\n',
        [
            "0.00\tNG\t2026-08-01 18:00:00\tAUS\tDM-Rankin\tRankin\tEpic\tXGame.xDeathMatch\tDeathmatch\t",
            "1.00\tC\t0\tLoque",
            "5.00\tSG",
            "10.00\tK\t0\tDamTypeRocket\t0\tRocketLauncher",
            // No EG line: the server died mid-match.
        ]));

        await using var db = NewContext();
        var result = await NewImporter(db).ImportFileAsync(path);

        Assert.Equal(ImportOutcome.Incomplete, result.Outcome);
        Assert.Equal(0, await db.Matches.CountAsync());
    }

    [Fact]
    public async Task Reuses_one_player_row_across_matches()
    {
        WriteLog("Stats_7777_2026_08_01_17_19_04.log", "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");
        WriteLog("Stats_7777_2026_08_01_18_19_04.log", "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher");

        await using var db = NewContext();
        await NewImporter(db).ImportDirectoryAsync(_dir);

        Assert.Equal(2, await db.Matches.CountAsync());
        // Two matches, same two people — the identities must not be duplicated.
        Assert.Equal(2, await db.Players.CountAsync());
        Assert.Equal(4, await db.MatchPlayers.CountAsync());
    }

    [Fact]
    public async Task Ignores_in_progress_log_files()
    {
        // The engine only renames to .log once the match ends.
        var path = Path.Combine(_dir, "Stats_7777_2026_08_01_19_00_00.log.tmp");
        File.WriteAllText(path, "0.00\tNG\tpartial");

        await using var db = NewContext();
        var results = await NewImporter(db).ImportDirectoryAsync(_dir);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Groups_both_fire_modes_under_one_weapon()
    {
        // Flak chunk and flak shell are two damage types from the same gun.
        var path = WriteLog("Stats_7777_2026_08_01_17_19_04.log",
            "10.00\tK\t0\tDamTypeFlakChunk\t1\tFlakCannon",
            "12.00\tK\t0\tDamTypeFlakShell\t1\tFlakCannon");

        await using var db = NewContext();
        await NewImporter(db).ImportFileAsync(path);

        var weapons = await db.MatchPlayerWeapons
            .Include(w => w.Weapon)
            .Include(w => w.MatchPlayer)
            .Where(w => w.MatchPlayer.NameUsed == "Loque")
            .ToListAsync();

        var flak = Assert.Single(weapons);
        Assert.Equal("Flak Cannon", flak.Weapon.DisplayName);
        Assert.Equal(2, flak.Kills);
    }

    [Fact]
    public async Task Records_kill_events_for_head_to_head()
    {
        var path = WriteLog("Stats_7777_2026_08_01_17_19_04.log",
            "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "12.00\tK\t1\tDamTypeShockBeam\t0\tShockRifle");

        await using var db = NewContext();
        await NewImporter(db).ImportFileAsync(path);

        var kills = await db.Kills
            .Include(k => k.KillerMatchPlayer)
            .Include(k => k.VictimMatchPlayer)
            .ToListAsync();

        Assert.Equal(2, kills.Count);
        Assert.All(kills, k => Assert.NotNull(k.KillerMatchPlayer));
    }

    [Fact]
    public async Task Deleting_a_match_removes_its_dependent_rows()
    {
        var path = WriteLog("Stats_7777_2026_08_01_17_19_04.log",
            "10.00\tK\t0\tDamTypeRocket\t1\tRocketLauncher",
            "11.00\tP\t0\tmultikill_1");

        await using (var db = NewContext())
            await NewImporter(db).ImportFileAsync(path);

        await using (var db = NewContext())
        {
            db.Matches.RemoveRange(db.Matches);
            await db.SaveChangesAsync();
        }

        await using (var verify = NewContext())
        {
            Assert.Equal(0, await verify.MatchPlayers.CountAsync());
            Assert.Equal(0, await verify.Kills.CountAsync());
            Assert.Equal(0, await verify.SpecialEvents.CountAsync());
            Assert.Equal(0, await verify.MatchPlayerWeapons.CountAsync());
            // Reference data survives — players and maps outlive any single match.
            Assert.Equal(2, await verify.Players.CountAsync());
        }
    }
}
