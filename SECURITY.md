# Security

Driftwall runs as the signed-in user, talks to photo providers over HTTPS, and stores API keys
encrypted with DPAPI so a copied `settings.json` does not leak them. It has no network listener and
no update mechanism that executes code.

If you find something that undermines any of that, please report it privately rather than in a
public issue: use GitHub's **Report a vulnerability** button on the repository's Security tab.
You will get an acknowledgement within a few days and a fix or a clear answer as soon as one exists.

Things that are not security issues: a provider serving an image you did not like, a provider's
rate limit, or the "Unknown publisher" warning on an unsigned build (see the README for why that
appears).
