using Ut2004Stats.Core.Domain;

namespace Ut2004Stats.Web.Components.Shared;

/// <summary>Presentation helpers shared across pages.</summary>
public static class Format
{
    /// <summary>Relative time, e.g. "3 hours ago". Falls back to a date past a week.</summary>
    public static string Ago(DateTime when)
    {
        var delta = DateTime.Now - when;

        if (delta.TotalSeconds < 0) return when.ToString("d MMM yyyy");
        if (delta.TotalMinutes < 2) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} minutes ago";
        if (delta.TotalHours < 24) return Plural((int)delta.TotalHours, "hour");
        if (delta.TotalDays < 7) return Plural((int)delta.TotalDays, "day");

        return when.ToString("d MMM yyyy");
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun} ago" : $"{count} {noun}s ago";

    /// <summary>Highlights the podium positions in ranked tables.</summary>
    public static string RankClass(int zeroBasedIndex) => zeroBasedIndex switch
    {
        0 => "rank-1",
        1 => "rank-2",
        2 => "rank-3",
        _ => "",
    };

    public static string Duration(double minutes) =>
        minutes >= 60
            ? $"{(int)(minutes / 60)}h {(int)(minutes % 60)}m"
            : $"{minutes:0.#}m";

    public static string TeamName(int team) => team switch
    {
        Teams.Red => "Red",
        Teams.Blue => "Blue",
        _ => "",
    };

    public static string TeamPillClass(int team) => team switch
    {
        Teams.Red => "pill pill-red",
        Teams.Blue => "pill pill-blue",
        _ => "pill",
    };

    public static string EndReason(MatchEndReason reason) => reason switch
    {
        MatchEndReason.TimeLimit => "Time limit",
        MatchEndReason.FragLimit => "Frag limit",
        MatchEndReason.ScoreLimit => "Score limit",
        MatchEndReason.Other => "Ended",
        _ => "Unknown",
    };

    /// <summary>Multi-kill tier names, as announced in game.</summary>
    public static string MultiKill(int level) => level switch
    {
        1 => "Double Kill",
        2 => "Multi Kill",
        3 => "Mega Kill",
        4 => "Ultra Kill",
        5 => "Monster Kill",
        6 => "Ludicrous Kill",
        >= 7 => "Holy Shit",
        _ => "",
    };

    /// <summary>Killing-spree tier names, as announced in game.</summary>
    public static string Spree(int level) => level switch
    {
        1 => "Killing Spree",
        2 => "Rampage",
        3 => "Dominating",
        4 => "Unstoppable",
        5 => "Godlike",
        >= 6 => "Wicked Sick",
        _ => "",
    };
}
