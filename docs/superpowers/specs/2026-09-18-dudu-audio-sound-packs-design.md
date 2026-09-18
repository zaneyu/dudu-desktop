# Dudu Audio Sound Packs Design

**Status:** Proposed
**Date:** 2026-09-18

## 1. Goal

Add a private, locally bundled sound system to Dudu Desktop with five cute
vocal sound packs sourced from the user's selected online clips:

1. `bubu-dudu-atata` from MyInstants;
2. `tata-lala` from Zedge;
3. `dudu-lalala` extracted from the selected TikTok video;
4. `dudu-atatata` extracted from the selected TikTok video; and
5. `dudu-yapapa` extracted from the selected TikTok video.

The companion should play short, restrained sounds for selected pet and
presentation events without blocking visual presentation, breaking quiet hours,
or making the pet noisy during normal work. The result is for this repository's
private-use desktop build only. It is not a public sound library, marketplace
asset, or Store-distributable content package unless separate redistribution
permission is obtained.

## 2. Current state and constraints

The current app is a self-contained .NET 10, WinUI 3, Windows 11 x64
companion. Visual animation is loaded from versioned PNG manifests and played
through `AnimationEngine`, `PetPresentationCoordinator`, and
`PresentationCoordinator`. Unsolicited remote-note and reminder events already
share `PresentationCoordinator`, while explicit pet actions use
`PetPresentationCoordinator`.

The existing dependency direction remains mandatory:

```text
Dudu.App -> Dudu.Infrastructure -> Dudu.Core
```

Audio playback is platform/application behavior and must not introduce a
Windows media dependency into `Dudu.Core`. The audio feature must preserve the
repository's private-use rules:

- do not publish or marketplace the copied audio;
- keep every non-fallback audio/provenance manifest at `privateUseOnly: true`;
- do not put audio in `assets/raw/`, which is reserved for the existing raw
  artwork workflow;
- do not fetch audio from third-party sites at runtime;
- do not log source URLs, clip contents, note text, tokens, or request bodies;
- do not let missing/corrupt audio prevent the app, overlay, notification, or
  animation from starting.

The audio feature must work when the relay is unavailable and must not add a
new timer or background polling loop.

## 3. Source set and provenance

The source records are explicit and retained with the private repository so a
future rebuild can identify which material was imported and how it was derived.

| Pack | Source page | Imported material | Intended role |
|---|---|---|---|
| `bubu-dudu-atata` | `https://www.myinstants.com/en/instant/bubu-dudu-atata-30881/` | The page's `bubu-dudu-atata.mp3` download, if available during asset preparation | Primary atata vocal cue |
| `tata-lala` | `https://www.zedge.net/notification-sounds/c426e3cb-b829-422e-8499-a81c720905fb` | The page's `Tata lala` sound, uploaded by `Alonsovz1` | Short greeting/notification cue |
| `dudu-lalala` | `https://www.tiktok.com/@bubududucorner/video/7615183028486212872` | The `lalala` section of the 15.83-second video | Soft greeting or affectionate acknowledgment |
| `dudu-atatata` | `https://www.tiktok.com/@bubududucorner/video/7615183028486212872` | The `atatata` section of the same video | Excited arrival or celebration |
| `dudu-yapapa` | `https://www.tiktok.com/@bubududucorner/video/7615183028486212872` | The `ya papa hmm ya papa` section of the same video | Playful reminder or cheeky interaction |

The TikTok page metadata identifies the video as “Bubu's cute sounds” and
describes the three sections as `lalala`, `atatata`, and `ya papa hmm ya papa`.
Exact timestamp ranges must be established during local asset preparation by
reviewing the downloaded media waveform. The app must never scrape TikTok or
depend on a TikTok media blob at runtime.

The provenance manifest must contain, for every source and derived cue:

- stable pack and cue identifiers;
- source page URL and direct media URL when the source exposes one;
- uploader/creator name when shown by the source;
- `accessedUtc`;
- original filename and media type;
- source SHA-256 and derived-file SHA-256;
- source duration and derived duration;
- source timestamp range for extracted cues;
- transformations such as trim, silence removal, resampling, channel mix,
  loudness normalization, and format conversion;
- `privateUseOnly: true`.

The manifest must describe the assets as copied/private-use inputs. It must not
claim that MyInstants, Zedge, TikTok, Bubu/Dudu's creator, or the uploader has
granted redistribution rights. The current plan deliberately keeps the audio
out of public/store distribution.

## 4. Asset layout and manifest contract

Keep source provenance separate from runtime assets:

```text
assets/sources/private-dudu-audio.json
src/Dudu.App/Assets/Audio/private-dudu/
  manifest.json
  bubu-dudu-atata/
    atata-01.wav
  tata-lala/
    tata-lala-01.wav
  dudu-lalala/
    lalala-01.wav
    lalala-02.wav
  dudu-atatata/
    atatata-01.wav
    atatata-02.wav
  dudu-yapapa/
    yapapa-01.wav
    yapapa-02.wav
```

The exact number of variants is determined by clean utterances in the source
clips. A pack may contain one variant. Do not manufacture variants by looping,
pitch-shifting, or text-to-speech synthesis in this phase; transformations are
limited to cleanup needed for reliable notification playback.

`manifest.json` is a new audio-only contract and must not expand the existing
PNG animation schema. It contains:

- `schemaVersion`;
- `packId`, `version`, `privateUseOnly`, and attribution/provenance summary;
- five pack entries with safe identifiers;
- ordered cue entries with relative `.wav` paths, SHA-256 hashes, durations,
  peak/loudness metadata, and a maximum simultaneous-playback policy;
- a default volume multiplier per pack;
- a reduced-volume fallback policy, not a second audio file.

The loader must reject rooted paths, traversal, symlink/reparse escapes,
unknown pack/cue keys, unsupported file extensions, missing hashes, files over
the size/duration limits, and `privateUseOnly != true`. It must validate RIFF/WAV
headers and the expected PCM format before exposing a cue to the player.

The first implementation should use short PCM WAV files rather than depend on
an additional codec package. The asset preparation tool may accept MP3/source
video inputs, but the application ships only normalized WAV derivatives.

## 5. Application architecture

Add an application-layer audio boundary under `src/Dudu.App/Audio/`:

- `IAudioCuePlayer` — narrow async interface for playing a cue key with
  cancellation and volume options;
- `AudioManifestLoader` — parses and validates the audio manifest and referenced
  WAV files;
- `AudioCueCatalog` — resolves pack and variant identifiers and chooses a cue;
- `WindowsAudioCuePlayer` — Windows implementation using the platform media
  playback API, with serialized playback and disposal;
- `AudioCueService` — policy layer for event-to-pack mapping, cooldowns,
  suppression, and best-effort error handling.

`Dudu.Core` must not reference these types. `PresentationCoordinator` and
`PetPresentationCoordinator` receive an optional audio callback/service from
the composition root. This preserves the existing presentation sequencing:

- the pet state transition and visual animation remain authoritative;
- audio is attempted after the visual presentation has been accepted;
- an audio failure is reported through the existing app error reporter and is
  never rethrown into note polling, reminder delivery, or explicit pet action;
- audio playback is not held under the pet-state semaphore longer than needed
  to enqueue a cue, so a slow media operation cannot block another visual
  action.

The composition root loads the private audio manifest after the visual pack is
validated. If the private audio manifest is absent or invalid, it constructs a
no-op player and continues with the existing visual-only companion behavior.
Safe mode also uses the no-op player.

## 6. Event mapping and playback policy

The initial mapping is intentionally sparse:

| Event | Pack candidates | Default behavior |
|---|---|---|
| Explicit greeting/welcome | `tata-lala`, `dudu-lalala` | Choose one cue with cooldown |
| Remote note arrival | `bubu-dudu-atata`, `dudu-atatata` | Play only when presentation is not suppressed |
| Reminder due | `dudu-yapapa`, `tata-lala` | Play only for a visible presentation |
| Celebration/success | `dudu-atatata`, `bubu-dudu-atata` | Prefer the atatata family |
| Manual pet interaction | `dudu-lalala`, `dudu-yapapa` | Play at most once per interaction window |

The service must enforce:

- one active Dudu vocal cue at a time;
- a global cooldown between cues, with a separate per-pack cooldown;
- no audio for queued-but-not-yet-presented notifications;
- no audio during quiet hours, pause, fullscreen-hidden state, session lock,
  or safe mode;
- no audio when the user disables sounds;
- a bounded volume multiplier, persisted independently from system volume;
- cancellation on shutdown and immediate cancellation of a superseded cue;
- no automatic playback for every ambient scheduler tick;
- no duplicate audio for a deduplicated presentation item.

Reduced motion does not automatically disable sound. It only changes visual
animation behavior. Quiet hours remain the authoritative user-facing silence
policy.

## 7. Preferences and persistence

Extend `Dudu.Core.Models.Preferences` with:

- `SoundsEnabled`, default `true`;
- `SoundVolume`, default `0.35`, clamped to `0.0..1.0`.

Persist both fields in the existing SQLite `preferences` row through a forward
schema migration. The migration must preserve existing preference values and
use the repository's normal pre-migration backup behavior. Older databases must
load the defaults when the new columns are absent during migration; malformed
values must be clamped or replaced with defaults rather than fail startup.

Expose both controls on the existing Appearance/settings surface. Saving the
settings must update `RuntimePreferencesState` and the audio service without
recreating the overlay or restarting the app. The UI must state that sounds are
local and can be disabled independently of Windows notification settings.

## 8. Asset preparation workflow

Asset preparation is a private, human-reviewed step, not a runtime feature:

1. Download the MyInstants and Zedge sounds through their visible download
   controls where available, and save the source URLs and access date.
2. Obtain the TikTok video through a user-authorized local workflow; do not add
   a scraper or automated TikTok downloader to the product.
3. Inspect each source waveform locally and mark clean, non-overlapping phrase
   ranges for the five packs.
4. Trim leading/trailing silence, apply a short fade-in/fade-out, convert to
   mono PCM WAV, normalize to a conservative peak/loudness target, and reject
   clips containing long music beds or abrupt clipping.
5. Write source and derived hashes to the provenance manifest.
6. Run the audio manifest validator and the focused audio tests.
7. Review the clips manually on Windows at default volume before enabling them
   in production composition.

The repository should not contain temporary downloads, source videos, browser
cache exports, or untrimmed audio. Only the deliberate derived WAV files,
runtime manifest, provenance manifest, and validation tests are retained.

## 9. Diagnostics and failure handling

Audio errors must follow the existing privacy-safe diagnostics rules:

- use operation names such as `audio-manifest-load`, `audio-cue-load`, and
  `audio-playback`;
- log only cue/pack identifiers, fixed failure categories, exception type, and
  HRESULT where applicable;
- never log source URLs, file contents, audio bytes, note content, or full media
  exception messages into privacy-sensitive logs;
- report errors through `IAppHostErrorReporter` when composed;
- fail closed to visual-only presentation on missing or broken audio;
- dispose the media player during normal shutdown and tolerate disposal errors.

The no-op fallback must be observable in tests but must not produce repeated
diagnostic noise on every presentation. A manifest failure should produce one
startup diagnostic and disable the audio service for that process.

## 10. Testing

### Core and infrastructure tests

- preference round-trip tests cover `SoundsEnabled` and `SoundVolume`;
- migration tests verify old databases receive defaults and new values survive
  save/reload;
- constructor/property tests verify volume clamping and invalid-value handling;
- structural tests ensure `Dudu.Core` has no audio/platform reference.

### App tests

- valid audio manifest loads and resolves all five pack ids;
- unsafe paths, missing files, bad WAV headers, hash mismatches, excessive
  duration, and `privateUseOnly: false` are rejected;
- the fake player records the selected pack/cue and never plays while quiet,
  paused, fullscreen-hidden, locked, disabled, or in safe mode;
- cooldown prevents duplicate and overlapping playback;
- cancellation and shutdown dispose cleanly;
- player failure does not fail the surrounding presentation;
- `PresentationCoordinator` and `PetPresentationCoordinator` retain their
  existing visual behavior when the player is absent or throws.

### Windows acceptance

On Windows 11 24H2 x64:

- startup with the private audio pack plays no sound before the overlay is
  ready;
- greeting, remote-note, reminder, celebration, and manual interaction each
  produce the expected pack or a documented eligible variant;
- quiet hours, pause, fullscreen hiding, lock/unlock, and safe mode suppress
  sound correctly;
- changing volume and disabling sounds takes effect without restart;
- repeated events do not overlap or become a sound storm;
- audio files are present in the installed publish tree and installer;
- a missing/corrupt private audio pack falls back to visual-only operation;
- the release checks reject a missing hash, unsafe path, or non-private audio
  manifest.

macOS verification is limited to manifest/parser tests, Core/infrastructure
  tests, and Windows-targeted stub builds. macOS cannot provide authoritative
  evidence for the real Windows media backend or installer contents.

## 11. Non-goals

This phase does not:

- discover or scrape additional TikTok/Zedge/MyInstants content at runtime;
- create a public soundboard or user-import UI;
- expose copied audio in a public Microsoft Store listing or marketplace;
- generate new vocal sounds with AI or text-to-speech;
- add background music, looping ambience, or a general-purpose audio mixer;
- modify the relay protocol or send audio over the network;
- add microphone recording or voice input;
- make sound a dependency of the Core pet state machine.

## 12. Acceptance criteria

The feature is accepted when:

1. all five private packs are represented by validated runtime assets and
   provenance metadata;
2. the app can start and operate visually when audio assets are absent or
   invalid;
3. supported presentation events play only eligible, non-overlapping cues;
4. quiet hours, pause, fullscreen-hidden state, session lock, safe mode, and
   user settings suppress audio correctly;
5. preference values survive restart and migration;
6. focused tests pass and the documented Mac-safe checks remain green;
7. the Windows build verifies actual playback and installer inclusion;
8. no copied source video, temporary download, public upload, or generated
   release artifact is committed;
9. the private-use boundary remains explicit in both manifests and release
   checks.
