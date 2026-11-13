# Privacy — Mayhem Advisor

Mayhem Advisor can send **anonymous** data to its own server, so the recommendations learn from more games and bugs
get fixed faster. It is **on by default** and you can turn it off at any time in **Settings → Share anonymous data**.
Nothing is sent until the app has shown you a notice about it, and nothing is sent at all while it's turned off.

## What is sent

**After each ARAM: Mayhem game** (a few KB):

- the game's patch and length;
- for the 10 players: champion, team, win/loss, augments, final items, physical and magic damage to champions,
  healing and skill order — **no names, no Riot IDs, no account or summoner information**;
- which of the 10 was you (so the app knows which build was yours);
- what the app saw during *your* game: the augment cards you were offered with the score it gave them, the one you
  picked, what it recommended buying and when, and the minute and unspent gold of your deaths;
- the app version and language (English or Spanish).

The game's ID is used only to avoid storing the same game twice. It is stored **encrypted** (HMAC with a secret key),
so it can't be used to look the game up.

**Every few hours, an error summary:**

- error and warning messages from the app's log, with your Windows user folder and your summoner name removed and
  numbers replaced by `#`;
- counters: how many times the app started, games seen, augment card sets read, picks saved, average screen-reading
  time, crashes;
- the app version, language and Windows build number.

**Only if you write it and press "Send"** (the "What do you think of Mayhem Advisor?" window after a game, or the
**Suggestions** tab). This is sent even with data sharing off, because you chose to send it:

- your rating (1–5 stars, optional), the kind (idea, bug or something else) and the text you wrote;
- if you leave "Attach my last game" ticked: that game's champion, augments, items, length, win/loss and the build the
  app recommended — no names;
- the app version, language and Windows build number.

The window after a game shows up at most once a day, and never again once you've sent feedback or pressed
"Don't ask again".

## What the app downloads by itself

So that its numbers stay fresh without a new version every few days, the app checks GitHub when it starts and every
6 hours for newer **data files** (champion, item and augment statistics) and downloads them. Nothing about you is sent
for this: it is a plain download from this repository's releases, and it works the same with data sharing turned off.
The app also checks there for new versions of itself, and only installs one when you click it.

## What is never sent

Your name, Riot ID, account, e-mail, chat, friends, screenshots, other programs, or anything from outside
League of Legends' ARAM: Mayhem games. The app has no account system.

## Where it goes and who sees it

To a Cloudflare Worker with a D1 database run by the author (ItzStorDz). Your IP address is **not stored**: it is only
used, encrypted with a key that changes every day, to limit how many uploads one connection can make per day.

- Games are shared back with all users (in the same anonymous form) so everyone's recommendations learn from
  everyone's games.
- Error summaries are only seen by the author, in a daily private report. They are deleted after 90 days.
- Feedback is only seen by the author, in the same daily private report. It is deleted after a year.
- Nothing is sold or shared with anyone else. The app never shows augment win rates (Riot's policy for third-party apps).

## Turning it off

**Settings → Share anonymous data.** From then on nothing more is sent. To ask for data to be removed, open an issue
at https://github.com/ItzStorDz/mayhem-advisor/issues — but note that, since nothing identifies you, the only way to
find "your" games is if you tell us which ones they are.

The server code is public: [cloud/src/index.js](cloud/src/index.js). The app side: [src/Overlay/Cloud.cs](src/Overlay/Cloud.cs).
