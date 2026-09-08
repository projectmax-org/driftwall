# Photo sources and their terms

Driftwall pulls photos from eleven places. Their licensing is **not** uniform, and that matters more
for a paid product than it does for a personal tool. This page is the summary you need before a
commercial release.

The app surfaces the same information in **Settings → Your sources**, and anything marked
**Restricted** is added switched off with a warning.

---

## Works out of the box, no key

| Source | Key | Terms | Verdict for a paid product |
|---|---|---|---|
| **Wallhaven** | Optional | Community uploads; per-image licence usually unknown | ⚠️ Fine as a user's own wallpaper. Never redistribute. Source page is always linked. |
| **Bing daily** | None | Microsoft licenses these for **personal desktop wallpaper only** | ⚠️ The app's own use is exactly what's permitted. Must never be exported, shared or resold. |
| **NASA APOD** | Optional | Generally public domain | ✅ Clear. Some entries are photographer-copyrighted; the app shows the credit. |
| **RSS feeds** | None | Depends entirely on the feed | ⚠️ Whatever the feed says. The app makes no licence claim. |
| **Local folder** | None | Your own files | ✅ Nothing leaves the machine. |
| **My collections** | None | Inherits the original source's terms | ✅ |
| **Reddit** | None | Free Data API tier **prohibits commercial use without written approval** | 🚫 **Off by default.** Needs written approval from Reddit before shipping. |

## Needs a free API key

| Source | Terms | Verdict for a paid product |
|---|---|---|
| **Unsplash** | Unsplash Licence permits commercial use | ✅ Attribution must stay visible, and the app must ping `download_location` on use — both implemented. Demo apps are capped at 50 requests/hour until Unsplash approves production access. |
| **Pixabay** | Content Licence permits commercial use, no attribution required | ✅ API terms cap caching at 24h and forbid permanent hot-linking; the app downloads before use, satisfying both. |
| **Flickr** | Licence is set per photo by the photographer | ⚠️ The app requests only reuse-permitting licences and records each photo's licence. Verify before any commercial use. |
| **Pexels** | API guidelines **specifically prohibit building a wallpaper app** on their content | 🚫 **Off by default.** Needs written permission from Pexels before shipping. |

---

## Before you put this on Steam

Three things need a decision, not a code change:

1. **Pexels and Reddit are the blockers.** Both are implemented and both are disabled by default with
   an in-app warning. Either get written permission, or delete
   `src/Driftwall/Sources/PexelsSource.cs` and `RedditSource.cs` and remove their entries from
   `SourceRegistry` and `ProviderTermsTable`. Shipping them enabled is the only genuinely risky thing
   in the app.

2. **Unsplash production access.** The 50 requests/hour demo cap is fine for you and unworkable for
   a few hundred customers. Apply for production access before launch.

3. **Whose API key?** Right now each user pastes their own key, which keeps you off the hook for
   rate limits and terms compliance. If you ever ship *your* key embedded in the binary, you inherit
   every user's usage against your quota and your agreement — and an embedded key in a desktop
   binary is extractable regardless of how it's stored.

The safest launch configuration is the current default: **Wallhaven + Bing**, both key-free, with
everything else opt-in.

---

## Adding another source

One file in `src/Driftwall/Sources/`, implementing `PhotoSourceBase`:

```csharp
public sealed class ExampleSource : PhotoSourceBase
{
    public override string Id => "example";
    public override string DisplayName => "Example";
    public override string Description => "What the user sees under the name.";

    public override async Task<PhotoPage> FetchAsync(
        SourceQuery query, SourceContext context, CancellationToken ct)
    {
        // Return PhotoPage.Empty for no results; throw SourceException for auth/rate-limit problems.
    }
}
```

Then add it to the list in `SourceRegistry`'s constructor and add a row to `ProviderTermsTable`.
Implementations must be stateless — one instance is shared by the whole app.
