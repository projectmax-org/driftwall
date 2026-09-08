namespace Driftwall.Core;

/// <summary>How freely a provider's photos may be used, and what the app must do about it.</summary>
public enum TermsLevel
{
    /// <summary>Free to use, including in a paid product. Attribution may still be required.</summary>
    Clear = 0,

    /// <summary>Usable, but with a condition the app must honour (attribution, no redistribution).</summary>
    Conditional = 1,

    /// <summary>The provider's terms restrict or prohibit this kind of product. Needs a decision.</summary>
    Restricted = 2,
}

public sealed record ProviderTerms(
    TermsLevel Level,
    string Summary,
    string? ActionRequired = null,
    string? TermsUrl = null);

/// <summary>
/// Per-provider licensing notes, surfaced in the source picker.
/// <para>
/// This is kept as a table rather than on <see cref="Sources.IPhotoSource"/> because it is a
/// commercial fact about the provider rather than a technical one about the code, and because it
/// needs to be reviewed as a whole before a paid release. Anything marked
/// <see cref="TermsLevel.Restricted"/> is off by default and shows a warning before it can be enabled.
/// </para>
/// </summary>
public static class ProviderTermsTable
{
    private static readonly Dictionary<string, ProviderTerms> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wallhaven"] = new(
            TermsLevel.Conditional,
            "Community uploads. Licences vary photo by photo and are often unknown.",
            "Fine for your own desktop. Do not redistribute or use commercially without checking the source page.",
            "https://wallhaven.cc/faq"),

        ["bing"] = new(
            TermsLevel.Conditional,
            "Microsoft licenses the daily images for personal desktop wallpaper only.",
            "Using them as your wallpaper is allowed. Exporting, sharing or reselling them is not.",
            "https://www.bing.com/wallpaper"),

        ["reddit"] = new(
            TermsLevel.Restricted,
            "Reddit's free Data API tier prohibits commercial use without written approval.",
            "Get written approval from Reddit before shipping this source in a paid product. Posts are user-submitted, so image rights vary.",
            "https://redditinc.com/policies/data-api-terms"),

        ["nasa"] = new(
            TermsLevel.Clear,
            "NASA imagery is generally public domain.",
            "Some APOD entries are copyrighted by the photographer; the app shows the credit when there is one.",
            "https://www.nasa.gov/nasa-brand-center/images-and-media/"),

        ["rss"] = new(
            TermsLevel.Conditional,
            "Licence depends entirely on the feed you point it at.",
            "Check each feed's terms yourself. The app makes no licence claim for feed content.",
            null),

        ["local"] = new(
            TermsLevel.Clear,
            "Your own files, on your own computer. Nothing leaves the machine.",
            null,
            null),

        ["collection"] = new(
            TermsLevel.Clear,
            "Photos you already saved. The original provider's terms still apply to each one.",
            null,
            null),

        ["unsplash"] = new(
            TermsLevel.Conditional,
            "The Unsplash Licence permits commercial use, free of charge.",
            "Attribution to the photographer and Unsplash must stay visible, and the app reports each use to Unsplash as their API terms require.",
            "https://unsplash.com/license"),

        ["pexels"] = new(
            TermsLevel.Restricted,
            "Pexels' API guidelines specifically prohibit building a wallpaper app on their content.",
            "Get written permission from Pexels before shipping this source. It is off by default for that reason.",
            "https://www.pexels.com/api/documentation/"),

        ["pixabay"] = new(
            TermsLevel.Conditional,
            "The Pixabay Content Licence permits commercial use without attribution.",
            "Their API terms cap caching at 24 hours and forbid permanent hot-linking; the app downloads before use, which satisfies both.",
            "https://pixabay.com/service/license-summary/"),

        ["flickr"] = new(
            TermsLevel.Conditional,
            "Licences are set per photo by the photographer.",
            "The app requests only photos under licences that permit reuse, and records the licence on each one. Verify before any commercial use.",
            "https://www.flickr.com/creativecommons/"),
    };

    public static ProviderTerms For(string providerId) =>
        Table.TryGetValue(providerId, out var terms)
            ? terms
            : new ProviderTerms(TermsLevel.Conditional, "Licence terms have not been reviewed for this source.");

    /// <summary>True when the source should be off by default and warn before being switched on.</summary>
    public static bool IsRestricted(string providerId) => For(providerId).Level == TermsLevel.Restricted;
}
