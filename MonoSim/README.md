# MonoSim

The game definition is [Data/monopoly-us-classic.json](Data/monopoly-us-classic.json).
It contains 40 spaces in clockwise order, all 28 purchasable properties, two
16-card decks, starting conditions, and financial rule constants. Amounts are
whole U.S. dollars. The app uses the built-in `System.Text.Json` parser.

The baseline uses U.S. Classic names, fixed Income Tax of $200 and Luxury Tax
of $100, and traditional pre-2021 decks including the $50 dividend Chance card.
It excludes Speed Die and Money Grab variants. Older editions can have different
taxes or card payments; current editions have rewritten Community Chest cards.
The edition ID identifies these choices rather than claiming all editions agree.

```powershell
dotnet run -- 4 aggressive,conservative,random --simulations 100 --seed 42
dotnet run -- --help
pwsh -NoProfile -File scripts/Test-GameData.ps1
dotnet build
```

The console entry point configures and runs simulation batches, writes delimited
outcomes, and logs each game through Serilog. `MonoSimEngine` also remains usable
through the API described below. Build and publish outputs include the game JSON,
schema, and application settings; default data loading uses the executable's
directory.

## Command line

```text
monosim <players> <strategies> [options]
```

The two positional arguments are required. `players` is an integer from 2 through
6. `strategies` is a case-insensitive comma-delimited list of one through
player-count values: `aggressive`, `conservative`, `infrastructure`,
`adjacent-group`, `blue-green`, `novice`, or `random`. An omitted player's entry
repeats the last strategy. Empty entries and unknown names are rejected.

For example, `4 aggressive,conservative` gives player 1 the aggressive strategy
and players 2–4 the conservative strategy. `4 random` draws independently for
each player in each game. Names default to `Player 1` through `Player 6` with
IDs `player-1` through `player-6`; starting cash, position, and jail status come
from the selected game definition. Each game receives fresh player records.

| Option | Default | Meaning |
| --- | --- | --- |
| `--simulations N`, `-s N` | `1` | Positive number of games. |
| `--maxrounds N`, `-r N` | `25 × players` | Positive round cap per game. |
| `--definition PATH`, `-d PATH` | Bundled definition | Custom game definition. |
| `--logroot PATH`, `-l PATH` | `logs` | Root for simulation and application folders. |
| `--output PATH`, `-o PATH` | `results.csv` | Results file. |
| `--delimiter CHAR` | Comma | Field separator, `tab`, or `\t`. |
| `--seed N` | Generated | Signed 32-bit batch seed. |
| `--shuffle-decks BOOL` | `true` | Enable deck shuffling, subject to the definition's shuffle setting. |
| `--on-error stop\|continue` | `continue` | Stop scheduling after a game fails, or run the remaining games. |
| `--help`, `-h` | — | Show usage without requiring positionals or creating files. |
| `--version` | — | Show version without running games. |

Logging flags appear in the next section. Options accept `--name value` and
`--name=value`; booleans require `true` or `false`. Short aliases accept values
the same way. Duplicate options, unknown flags, integer overflows, and extra
positional arguments are rejected. Relative paths resolve from the working
directory, except the default bundled definition and settings paths.

Up to `min(4, Environment.ProcessorCount, simulations)` games run concurrently.
Each engine and its strategy instances are isolated. The batch seed and zero-based
simulation index derive each game's seed using unchecked 32-bit
`batchSeed + index * 7919`. Strategy selection uses a separate seeded stream;
random strategy draws do not consume dice or deck randomness. Each simulation
also shuffles its turn order using a separate stream seeded with
`simulationSeed ^ 0x5455524E`. Player IDs, names, and assigned strategies stay
together, and results columns continue to refer to the original player numbers.
The chosen order is recorded in that simulation's log. For the same
application version, definition, options, and batch seed, results are reproducible
regardless of worker scheduling. Simulation IDs and application run IDs are fresh
correlation identifiers rather than seeded values.

## Results and runtime errors

Before any simulations start, an existing output file prompts for append,
overwrite, or a new filename; the new-name option repeats the existence check.
Quit or EOF cancels. If standard input is redirected, an existing path fails
without modifying the file. Append validates the exact header, selected delimiter,
UTF-8 encoding, complete quoted fields, and every existing row's field count.
An empty existing file receives a header after append is explicitly chosen.

Results are UTF-8 with a header and one row per simulation, written in completion
order. A single synchronized writer flushes every completed row. Delimiters,
quotes, and embedded newlines in field values are escaped with CSV-style quoting.
The fixed schema supports appending batches with different player counts:

- `ResultSchemaVersion`, `RunId`, `SimulationId`, `SimulationNumber`, and `Status`.
- `BatchSeed`, `SimulationSeed`, `DefinitionId`, `DefinitionSchemaVersion`,
  `DefinitionPath`, `PlayerCount`, `MaxRounds`, and `ShuffleDecks`.
- `RoundsPlayed`, `TurnsPlayed`, `EndReason`, `WinnerIds`, `ErrorType`,
  `ErrorMessage`, and `LogPath`.
- For each of six player slots: `PlayerNStrategy`, `PlayerNCash`,
  `PlayerNPortfolioValue`, `PlayerNBankrupt`, and `PlayerNWinner`.
- Twelve additional columns record `PlayerNAdherence` and
  `PlayerNEffectiveAdherence` for each of the six player slots.

The current results schema is version 2. Version 1 files have a different
header and cannot be appended to; choose a new filename or overwrite them.

Unused player slots are blank. Actual randomly selected strategies are recorded.
Shared winners appear as pipe-separated IDs and each winning player's flag is
true. Monetary fields use invariant formatting. Failed games have status `failed`,
their error details and configuration, and blank final-state fields; they are
never assigned a fabricated winner. Cancellation during a game uses status
`cancelled`. Appending does not renumber prior batches; `(RunId, SimulationNumber)`
identifies an attempt within its batch, and `SimulationId` links directly to logs.

With `--on-error stop`, already active games finish and record their results while
no additional games begin. File-writing failures abort the batch. Ctrl+C stops
scheduling and requests cancellation at engine turn/roll boundaries; completed
rows remain in the results file. Logs drain and flush after workers finish.
Exit codes are 0 for success, 1 for runtime/configuration/file errors or any failed
game, 2 for invalid command-line arguments, and 130 for cancellation. No rows are
created for simulations that never start.

## Logging configuration

Serilog writes newline-delimited compact JSON into one folder per simulation,
relative to the working directory:

```text
logs/monosim-<SimulationId>/run.clef
logs/monosim-<SimulationId>/run_001.clef
logs/monosim-<SimulationId>/run_002.clef
```

The folder uses the engine's full `SimulationId`, which also appears in its
events and final game state. Serilog adds numbered suffixes when rolling files
by size. Startup, progress, and other messages without a simulation ID go to
`logs/application-<RunId>/run.clef` and its rolled files. `--logroot results/logs`
changes the root to `results/logs` while retaining `run.clef` as the base filename.
The old `--log-file` option is replaced by `--logroot`.

Engine fields remain separate JSON properties, including
simulation ID, event sequence, round, turn, roll, player, signed amount, and new
cash balance. `RunId` correlates events from one application invocation;
`SchemaVersion` identifies the logging envelope. Exceptions include stack details.

Defaults come from `appsettings.json` beside the executable, followed by
`MONOSIM_` environment variables, then command-line overrides. The settings file
is copied to build and publish outputs. Environment nesting uses double
underscores, for example `MONOSIM_Logging__BufferSize=2048`.

| Command-line option | Logging setting | Default |
| --- | --- | --- |
| `--log-config` | Settings file path | Executable's `appsettings.json` |
| `--logroot`, `-l` | `RootDirectory` | `logs` (or configured `FilePath` parent) |
| `--log-level` | `FileMinimumLevel` | `Information` |
| `--log-console-level` | `ConsoleMinimumLevel` | `Warning` |
| `--log-console-enabled` | `ConsoleEnabled` | `true` |
| `--log-progress` | `ShowProgress` | `true` |
| `--log-buffer-size` | `BufferSize` | `10000` events |
| `--log-file-size-limit` | `FileSizeLimitBytes` | `67108864` bytes (64 MiB) |
| `--log-retained-files` | `RetainedFileCountLimit` | `null` (retain all) |
| `--log-max-open-files` | `MaxOpenFiles` | `8` writers |

```powershell
dotnet run -- 2 novice -l logs/example --log-buffer-size 2048
dotnet run -- 2 novice --log-config appsettings.json --log-progress false
```

Levels use Serilog names: `Verbose`, `Debug`, `Information`, `Warning`, `Error`,
and `Fatal`. Keep the file level at `Information` or lower to retain the complete
simulation history. The console normally shows warnings/errors on stderr and
explicit `MonoSim.Progress` information messages on stdout; routine engine
events stay in the file. Console errors are concise; JSON records retain full
exception details. Console and file thresholds are independent.

File routing runs behind one bounded queue with `blockWhenFull: true`. When the
writer falls behind, producers wait instead of dropping events. At most
`MaxOpenFiles` file writers remain open; others are closed and reopened as needed
without losing their file sequence. Files roll at the configured size, with no
automatic deletion unless a retention count is set. Retention applies separately
within each simulation or application folder. One event can exceed the size
limit. Engine-generated identifiers keep concurrent simulations in separate
folders without changing the engine's logger contract.

Application shutdown drains and flushes queued events on success and handled
errors. Invalid configuration or inaccessible paths produce a concise stderr
message and exit code 1. Serilog's internal diagnostics also go to stderr and
cause a nonzero application exit. Forced process termination can still lose
buffered events. Logging does not provide a durable transaction journal.

API callers can use the same pipeline while keeping the engine's `ILogger`
constructor unchanged:

```csharp
using Microsoft.Extensions.Logging;
using MonoSim.Logging;

await using var logging = new SimulationLogging(LoggingOptions.Load());
var engine = new MonoSimEngine(players, logging.Factory.CreateLogger<MonoSimEngine>());
var final = engine.Play();
var simulationLogPath = logging.GetSimulationFilePath(final.SimulationId!);
```

Dispose the logging session after all simulation producers have finished. A
session may be shared across multiple simulations; routing uses each event's
`SimulationId` automatically, including interleaved simulations. IDs used for
folder lookup must contain at most 128 ASCII letters, digits, hyphens or
underscores. Engine-generated IDs satisfy this requirement. Outcome CSV/TSV
export uses the separate `ResultsWriter`.

## Contract

[Data/monopoly.schema.json](Data/monopoly.schema.json) is a Draft 2020-12 JSON
Schema defining required fields and effect variants and rejecting unknown fields.
The PowerShell 7.4+ validation script checks the schema and cross-reference,
financial, board, and deck invariants without additional packages.

`schemaVersion` versions the contract; `edition.id` identifies the rules and card
set. Record both with future simulation results. Stable space/card IDs support
structured logs. `position` is zero-based: GO is 0 and Boardwalk is 39.
Movement wraps modulo 40. Properties are embedded in their spaces so each has
only one definition.

Street `rent.houses[0..3]` means one through four houses. Base, unimproved
complete-group, and hotel rents are separate values. Complete-group rent applies
only to unimproved streets, even if another street in the group is mortgaged.
The mortgaged space itself earns no rent. Railroad and utility arrays use
**the number owned minus one** as the index. Ordinary utility rent multiplies
the movement dice total.

`hotelPrice` is additional cash to upgrade a street already holding four houses;
those houses return to the bank. It is not the total cost from an empty street.
Buildings sell back at `buildingResaleFraction` of purchase cost; a hotel represents
five building purchases. Repairs count actual houses and hotels, without also
charging for houses beneath a hotel.

`mortgageValue` is the cash received. Redemption costs principal plus
`mortgageInterestRate` of principal. A recipient of mortgaged property owes that
interest immediately; if they defer redemption, interest is charged again when
redeeming. Rates are decimal fractions, not money amounts; use .NET `decimal`
for financial calculations. Building prerequisites and finite supplies are
included in `rules` and `initialState`.

## Effects and state

`effects` is an ordered list; descriptions are labels, not executable text.

| Type | Interpretation |
| --- | --- |
| `transfer` | Move positive `amount` from `from` to `to`. `eachOtherPlayer` means that amount separately for every active opponent, excluding the current player. |
| `moveTo` | Move forward to `destinationId`, then resolve that space. |
| `moveToNearest` | Move forward to the next matching `spaceType`, wrapping if needed. The rent override applies only to this landing. |
| `moveRelative` | Move the signed number of spaces, then resolve the destination, including another card draw. |
| `goToJail` | Enter Jail as a prisoner, collect no GO salary, and end the turn. |
| `repairs` | Pay the bank `perHouse` times houses owned plus `perHotel` times hotels owned. |
| `retain` | Hold this physical card outside its deck until used or traded. After use, return it to its original deck's bottom. |

On forward movement, `collectGo: true` enables the normal `goSalary` for crossing
or reaching GO, once per circuit. A card moving to GO awards salary through
movement alone; do not add another payment when resolving GO. Initial placement
on GO pays nothing. Landing on Jail normally is just visiting. Tax payments go
to the bank and Free Parking pays nothing.

Nearest-railroad cards double normal rent. The nearest-utility card uses ten times
a new two-dice roll. Overrides apply when rent is owed: self-owned and mortgaged
destinations charge nothing; unowned destinations still allow purchase or auction.
Two separate nearest-railroad IDs preserve both physical cards in the deck.
Ordinary cards return to the bottom after resolution. Shuffle each deck at setup;
the JSON array order is not a prescribed draw order. A recorded random seed can
later make simulations reproducible.

This is a reusable definition, not mutable game state. Each simulation should
create fresh balances, ownership, mortgages, buildings, and deck order from
`initialState`. Keep held cards separately and preserve their original deck IDs.

## Player records

`MonoSim.Models.Player` holds a player's string ID, name, strategy identifier,
cash balance, total mortgage value, zero-based board position, owned property
IDs, completed laps, and financial ledger. Monetary fields use `decimal`. Identity, strategy,
and cash balance are required at construction; initialize cash from the game
definition's `initialState.playerCash`. Property and ledger arrays start empty.
`CompletedLaps` starts at zero and counts forward crossings of GO, including card
movement, independently of whether salary is collected. Backward movement and
jail transfers do not increment it. The count is preserved in player snapshots.

`TotalMortgageValue` is the sum of the listed mortgage values of **all** owned
properties, including ones currently mortgaged. It is not outstanding mortgage
debt or remaining borrowing capacity. Property IDs reference board space IDs.

Each `Transaction` contains `Description`, signed `Amount` (negative for a debit,
positive for a credit), and `NewCashBalance` after the transaction. Ledger entries
are chronological. These records store snapshots; the engine appends
the transaction and update `CashBalance` together, and recalculate mortgage value
when ownership changes. Starting cash is the opening balance, before any ledger
entries, unless explicitly recorded as an opening credit from zero.

Records use init-only properties and support `System.Text.Json` serialization.
Arrays remain mutable in .NET: when creating a new snapshot with `with`, replace
changed arrays instead of mutating arrays shared with an earlier snapshot.

```csharp
using MonoSim.Models;

var player = new Player
{
    Id = "player-1",
    Name = "Alice",
    Strategy = "aggressive",
    CashBalance = 1500m // In game setup, read this from initialState.playerCash.
};

var rent = new Transaction("Rent payment", -50m, player.CashBalance - 50m);
player = player with
{
    CashBalance = rent.NewCashBalance,
    FinancialLedger = [.. player.FinancialLedger, rent]
};
```

## Observable game state

`MonoSim.Models.GameState` is the view passed to both `IPlayingStrategy.PreRoll`
and `IPlayingStrategy.PostRoll`, alongside the acting player's own `Player` record.
Its `OtherPlayers` array excludes the acting player and contains every other
player's record. Their current positions are available through
`OtherPlayers[i].BoardPosition`, keeping one source for each position.

Its `Properties` array contains a `PropertyState` for every purchasable space,
including streets, railroads, and utilities. Each record has `PropertyId`,
`PropertyName`, decimal `SalePrice`, `HouseCount`, `HotelCount`, `IsOwned`, and
`IsMortgaged`.
`IsAvailable` is computed as the inverse of `IsOwned`, so the two flags cannot
disagree. Sale price is the printed purchase price, not a negotiated trade or
auction price. Hotel properties have zero houses; railroads and utilities have
zero houses and hotels. Snapshot construction should populate the arrays from
the current game state and copy mutable arrays when isolation is needed.

```csharp
Action PreRoll(Player player, GameState gameState);
Action PostRoll(Player player, GameState gameState);
```

Each method returns a `System.Action` delegate for the game engine to invoke.
Strategy methods select the action; the engine executes it to update game state.

## Aggressive strategy

`AggressiveStrategy` spends current cash only and does not mortgage owned
properties to raise funds. On the first trip around the board, its priorities are:

1. Buy the available property landed on when affordable, regardless of whether
   it completes a color group.
2. Buy affordable mortgaged properties through accepted offers from other players,
   as a secondary way to accumulate properties and eventually form monopolies.
3. Spend remaining cash on mortgage redemption and legal house/hotel upgrades,
   choosing the greatest immediate ordinary-rent increase per dollar first.

Completing a color group does not raise first-lap purchase priority. On later laps,
the player considers affordable unowned properties reachable from the current
position by the next direct dice roll. It favors opportunities in groups where
it already holds more properties, including railroad and utility sets. Among
equally useful opportunities, it reserves the cheapest purchase price. Card
movement and future GO salary are not assumed. Opportunities are recomputed after
each action, so purchases by other players or changed cash do not leave stale
reservations.

An affordable landing purchase is passed when it would prevent buying a stronger
reachable opportunity. For example, after landing on Connecticut with $200 and
owning States Avenue, the player keeps $140 for available St. Charles Place or
Virginia Avenue rather than spending $120 on Connecticut. At $260 it buys
Connecticut and retains $140. If the alternatives are unavailable, unreachable,
or unaffordable even before the purchase, they do not block it. An actual bank
landing that completes a monopoly can use the reserved cash; mortgage purchases
and trades must still protect their reserve even when they would complete a group.

Mortgage purchases, cash-spending trades, and auctions of owned titles preserve
cash for strengthening any existing group. Auctions of bank-owned properties use
the same opportunity comparison as landing purchases, so acquiring the desired
property itself can use the reserved cash. Building upgrades and mortgage redemptions
preserve it only when a reachable unowned property would complete a monopoly.
Partial-group opportunities therefore do not prevent developing existing income.
These conditional reserves apply after the first GO crossing; there is no fixed
minimum cash balance. Debt settlement and the established jail-release policy
retain priority.

Mortgage trades follow affordable landing purchases that pass these checks.
The policy has no fixed turn-number
cutoff for trading: it considers a trade when the engine offers one and cash
remains after any landing purchase. Equal investment returns use lower cost,
then ordinal option ID, for deterministic decisions. Money left over because
no legal option is affordable stays in the player's cash balance.

Before rolling, an imprisoned player uses a held Get Out of Jail Free card if
the engine offers that option; otherwise they pay the affordable jail fine.
`Player.IsInJail` distinguishes imprisonment from visiting the Jail space. If
release is unaffordable, they preserve cash and leave release-by-dice handling
to the engine. A newly jailed player makes no post-roll investments, preserving
cash for release before the next roll. After a successful pre-roll release,
the strategy may spend the remaining cash on offered investments.

The engine supplies `GameState.AvailableActions` for the current player and turn
phase. These are legal options with exact total costs, including mortgage
transfer interest or redemption interest. They may be unaffordable. In particular,
the engine must offer bank purchases only on the appropriate landing, mortgage
trades only with seller agreement, jail cards only when held, and construction
only when ownership, mortgages, even building, and bank stock permit it. A bought
mortgaged property remains mortgaged until a separate redemption option executes.

Inject an `IStrategyActionExecutor` when constructing the strategy. Invoking the
returned `Action` runs the chosen option through that executor, which applies
the changes and returns `StrategyTurnState` containing the updated player and
fresh game state. The strategy then selects the next affordable option until
none remains. Each option is re-evaluated after execution, so purchases and
construction can unlock further legal options. The executor updates financial
ledgers, ownership, total mortgage values, buildings, and cards; the strategy
does not mutate the input snapshots. A stale/rejected option must be rejected
by the executor. Returned actions are intended to execute once, in the turn
and phase for which they were selected.

`MonoSimEngine` implements this execution interface, including current-state
validation, cash settlement, trade negotiation, and receipt history.
The package-free strategy checks use a test executor and can be run with:

```powershell
dotnet run --project Tests/MonoSim.StrategyTests/MonoSim.StrategyTests.csproj
```

## Conservative strategy

`ConservativeStrategy` focuses on the first color group acquired by landing on
a street. Railroads and utilities do not establish a focus. It buys other landed
streets in that group and considers accepted trades that increase its ownership
of the group. It does not give away streets from the focus group or speculate on
unrelated purchases. Once the group is owned, it develops that group before
starting another; another group can be started only after every street in the
current group has a hotel and is unmortgaged.

An unrelated landing purchase is allowed only when it is the last available
street in its group and has an explicit, nonbinding `FollowUpTrade` plan. That
exchange must use the purchased street, complete the conservative player's focus
group, and increase the other player's ownership of a color group they already
hold. This is planning only: the engine may publish an actual offer only after
the prerequisite purchase, when the player owns every offered asset. Execution
requires fresh validation and counterparty acceptance; the plan is not a binding
promise of future assets or cash.

`PropertyTrade` records the counterparty, properties and cards given and received,
cash paid and received, and each party's mandatory bank fees. Trades are atomic
engine actions. The shared valuation rules below provide reference prices for
negotiation, with mandatory bank charges paid separately. Both positive and negative net
cash costs are supported; an exchange can credit the player's ledger. The engine
still verifies seller consent, funding, mortgage charges, and ownership before
executing. Normal focus-group trades can be cash-only purchases; the requirement
to help the counterparty's monopoly applies to unrelated setup purchases.

There is no fixed cash reserve. Focus acquisitions can spend existing cash, but
development and mortgage redemption use only the positive difference between
current cash and cash at the start of this player's full turn. At most **one**
house or hotel upgrade executes in that full turn, across both strategy phases.
Legal upgrades favor streets with fewer houses. This conserves cash during
ordinary turns while allowing initial monopoly assembly to cost cash. It is a
decision rule, not a guarantee about how often a simulated turn will be profitable.

An imprisoned conservative player selects no voluntary release action, including
when holding a jail card. The engine handles dice attempts, release on doubles,
and any mandatory fee after the third failed attempt. The strategy does not
extend imprisonment beyond that limit.

Populate `PropertyState.ColorGroup` from the game definition. Supply a distinct
`GameState.TurnNumber` for each full player turn, unchanged across its phases
and any extra doubles rolls. Invoke the pre-roll delegate even when it performs
no transaction: that captures opening cash. Keep `OwnedPropertyIds` in acquisition
order. Create a fresh strategy instance for each simulation; its chosen group,
opening cash, and last upgrade turn are private decision memory, separated by
player ID. `MonoSimEngine` implements trade execution and current-asset offers
through `IStrategyActionExecutor`; the default console entry point remains an
inventory display rather than a simulation user interface.

The existing strategy-check command also runs conservative scenarios covering
target persistence, trades and setup purchases, cash-flow budgeting, full-turn
upgrade limits, hotels, and jail waiting.

## Infrastructure strategy

`InfrastructureStrategy` seeks all four railroads and both utilities before
developing color groups. It prioritizes purchases of infrastructure and accepted
trades that exchange color properties for missing infrastructure. Infrastructure
is never offered away. Color landing purchases are allowed as speculative trade
leverage without an agreed follow-up deal. Accepted offers to cash out held color
properties take priority over buying more leverage, with older holdings disposed
of first. The strategy acts on available engine offers; it cannot force another
player to accept a trade or guarantee a maximum holding period.

Until both infrastructure sets are owned, color mortgage purchases and
construction are deferred. The player may redeem infrastructure mortgages to
earn rent when doing so preserves the reserve. After **both** sets are fully
owned, the strategy switches to forming color monopolies through available
landing purchases, mortgage purchases, and trades. New monopoly completions take
priority over other color acquisitions. Color trades preserve existing complete
groups and must increase progress toward further monopolies, measured by the
sum of squared ownership counts within color groups. Legal development is then
allowed, favoring the rent increase per dollar spent. Completion means ownership;
mortgages are redeemed through separate actions.

The reserve is the printed purchase price of the nearest **unowned bank-held**
railroad or utility clockwise ahead, wrapping past GO. It excludes properties
already owned by any player and does not anticipate a GO salary or future rent.
The reserve is recalculated against projected ownership after every option,
including an infrastructure purchase itself, and checked again using refreshed
state after execution. Thus buying the current railroad may be skipped if it
would leave too little to buy the next utility. If none remains bank-held, this
reserve is zero even if other players still hold infrastructure being sought
through trades. Mortgage-transfer fees count in the action's cash cost.

If mandatory game charges have already depleted the reserve, the strategy stops
discretionary spending but permits cash-raising sales that do not worsen the
shortfall. This policy protects reserves when choosing optional actions; it
cannot prevent forced taxes, rent, or other engine charges from reducing cash.

In pre-roll, a jailed player seeks early release only if an unowned railroad or
utility is ahead of Jail **before the next GO crossing**. A held free-release
card is preferred; otherwise the offered jail fine is paid only if the purchase
reserve survives. If only Reading Railroad behind Jail remains unowned, or all
infrastructure is owned, the player waits. The engine handles doubles attempts
and mandatory release after the third failed attempt. A newly jailed player
waits until their next pre-roll to make this decision.

Populate `PropertyState.Type` (`Street`, `Railroad`, or `Utility`) and its
`BoardPosition` from the game definition's `type` and `position` fields. Supply
the complete property array and `GameState.BoardSize` (40 in this baseline).
These fields let the strategy distinguish infrastructure and compute clockwise
distances without hard-coded property IDs. Infrastructure checks load the actual
board data and cover purchase reserves, trades, leverage disposal, phase
transitions, and conditional jail release alongside the existing strategy checks.

## Adjacent group strategy

`AdjacentGroupStrategy` anchors its targets on the first color property acquired
and the next color group clockwise. Groups may straddle a corner or GO. For
example, light blue pairs with pink; dark blue pairs with brown. Once chosen,
the pair persists for that player. A declined or unaffordable first purchase
does not commit a pair. Create a new instance per simulation; decision memory
is separate for each player ID. Existing players seed their focus from the first
color property in their acquisition-ordered `OwnedPropertyIds`.

The player buys and trades for both target groups before pursuing their secondary
objective. Outside landing purchases are allowed as speculative leverage, with
accepted trades or cash sales used to dispose of that leverage promptly. These
purchases remain possible while working on the secondary objective. Target streets
and any already acquired contained infrastructure are never traded away.

Contained infrastructure means **every railroad or utility within the clockwise
span** from the first street of the first group to the last street of the second.
It includes utilities embedded within a group, not just the gap between groups.
For pink/orange, both Electric Company and Pennsylvania Railroad qualify.
Some consecutive pairs, such as orange/red, contain no infrastructure.

After both groups are owned, acquiring a contained asset and improving target
streets have equal priority. Among these options, lower cash cost wins, followed
by ordinal option ID; neither action category has a fixed preference. The engine
supplies legal building options, enforcing even development, bank stock, and
mortgage restrictions. Target mortgage redemptions are considered to enable
rent and legal development.

The primary strategy is complete when both groups are owned and **either** at
least one contained railroad/utility is owned **or** every target street has an
unmortgaged hotel. Owning all contained assets is not required. Pairs containing
no infrastructure finish through full development. After completion, additional
color monopolies can be pursued through mortgage purchases, accepted trades, and
available landing purchases. Completion is re-evaluated after each action, so
expansion can begin immediately after satisfying either route. Remaining target
upgrades and contained acquisitions are still considered; new monopoly completions
receive priority over ordinary acquisitions.

The cash reserve protects the printed price of the nearest unowned street in
either target group clockwise ahead, wrapping past GO. The same reserve checks
as Infrastructure Strategy apply, including recalculation after purchases, trade
fees, and cash-raising sales when forced charges have depleted cash. No reserve
is needed for streets already owned by another player; acquiring those requires
an accepted offer. The initial purchase must also leave the reserve for the next
street in its proposed pair. The player does not assume future GO salary or rent.

The jail condition is identical to Infrastructure Strategy: release in pre-roll
only when unowned infrastructure lies ahead of Jail before GO, prefer a card,
otherwise pay the fine if the target-street reserve survives. The release trigger
continues to refer to railroads/utilities, not target color streets. Otherwise
the engine handles doubles attempts and mandatory third-attempt release.

The strategy checks use the actual board definition to cover consecutive pairs,
wraparound, contained assets, leverage, reserve boundaries, equal secondary
priorities, both completion routes, and jail behavior. Engine integration uses
the existing `IStrategyActionExecutor` contract.

## Blue-Green strategy

`BlueGreenStrategy` is the fixed-target variant of Adjacent Group Strategy. It
always targets Pacific Avenue, North Carolina Avenue, Pennsylvania Avenue,
Park Place, and Boardwalk (`green` and `darkBlue` in the definition), regardless
of previously acquired properties. Green and dark blue acquisitions can be
interleaved; completing one monopoly is not a prerequisite for buying into the
other. Short Line is the contained infrastructure for this pair.

Before reaching Pacific Avenue on the final side, optional spending must leave
at least **$400** while any of the five targets remains unowned by the player.
This floor also applies to leverage purchases, target trades made elsewhere,
and voluntary jail fines. Accepted cash sales can restore an already depleted
fund. Forced engine charges can still reduce cash below the floor; the policy
then stops optional debits until the reserve is restored.

On the final side, a target purchase or accepted trade acquiring a target may
spend the reserved cash, provided its full net cost is affordable. It need not
leave enough to buy the next target: the player may buy one or several as funds
allow. Outside purchases and other spending still preserve the printed price of
the next bank-available target. No future GO salary or rent is assumed. After GO,
the $400 pre-arrival floor applies again if targets remain to be acquired.
Once all five are owned, the acquisition floor is removed so it does not block
development or the remaining secondary objective.

Leverage handling, target protection, legal upgrades, and completion rules reuse
Adjacent Group Strategy. After owning both groups, buying Short Line and improving
a target have equal priority. Either owning Short Line or fully improving all
five target streets completes the primary strategy and unlocks further monopolies.
The same conditional jail-release trigger is inherited; paying the fine also
respects the applicable Blue-Green reserve. The implementation shares the adjacent
strategy's decision logic and overrides its target selection and reserve policy.

The strategy-check command includes fixed targets, both acquisition orders,
reserve boundaries, trades and fees, final-side spending, subsequent circuits,
development, expansion, and jail decisions for this variant.

## Custom trade rules

Schema version 2 adds `rules.trades` to the game definition. These are project
rules supplied for the simulations, including permission to exchange improved
properties with their buildings attached. All strategies share `TradeValidator`
for currently executable trades, including the legacy `BuyMortgagedProperty`
action. A cash purchase of a mortgaged property pays its principal to its current
owner and the transfer interest separately to the bank.

Trades have exactly two participants: the acting player and `OtherPlayerId`.
Only their currently owned assets and current cash can be offered. The terms
support cash, any mix of properties, and held Get Out of Jail Free cards. They
have no future-income, rent-waiver, or third-party fields. JSON deserialization
rejects unknown consideration fields instead of silently ignoring them.

| Asset | Reference value |
| --- | --- |
| Unimproved, unmortgaged property | Printed purchase price |
| Improved property | Printed price plus resale value of its buildings |
| Mortgaged property | Mortgage principal, excluding interest |
| Get Out of Jail Free card | $50 per held card |

Buildings use `BuildingResaleFraction` from the rules. A hotel represents the
four exchanged house purchases plus its additional hotel price; count that
investment once, without adding nonexistent houses. Mortgaged properties remain
unimproved under the existing mortgage prerequisite. Populate `MortgageValue`,
`HousePrice`, and `HotelPrice` in each property snapshot from the definition.
Player records now include `HeldGetOutOfJailFreeCardIds`, using the original deck
card IDs so cards can later return to the correct deck.

Reference values support negotiation, portfolio scoring, and liquidity calculations;
they no longer impose equal-value trades. Parties may agree on different prices
or unequal asset exchanges.
`BankFees` is the acting player's mortgage-transfer interest;
`OtherPlayerBankFees` is the counterparty's. Each recipient owes interest on
the mortgaged properties they receive. These charges are separate bank payments.
Both cash balances after net settlement and fees must be nonnegative.
Load `GameState.BuildingResaleFraction`, `MortgageInterestRate`, and
`GetOutOfJailFreeCardTradeValue` from the definition's corresponding rule values.

`RecordOffer` validates current ownership and funding before retaining an offer.
A counteroffer's `OriginalOfferId` must identify the root original offer, with
the same two participants. Its combined given/received properties must include
at least one property appearing on either side of that original offer. Either
or both original properties may be retained, regardless of direction. Every
counteroffer is still independently validated for ownership, funding, and fees. Cash/card-only originals
have no property that could satisfy this counteroffer requirement.

`RecordCompletedTrade` validates and records receipts without moving assets.
The executor must atomically transfer ownership, buildings, cards, cash,
and bank fees, update both financial ledgers and total mortgage values, and
persist that receipt history in its returned state. `MonoSimEngine` and its
integration tests implement this contract. Offer and
history helpers copy asset arrays so callers cannot rewrite their original
terms by modifying the arrays used to submit them.

`GameState.CompletedTrades` covers the entire current full turn, across pre-roll,
post-roll, and extra rolls. A property received in a trade cannot be offered to
a **different** counterparty during that turn, whether the pass-through player
is the acting player or their counterparty. Directly returning it to the same
player is not a third-party pass-through under this rule. A receipt from an
earlier turn may be traded, and a property just purchased from the bank may be
used immediately as leverage. The engine must validate both offers and execution
against current state, retaining the root original offer while counteroffers
are being negotiated. Do not reset receipt history between turn phases.

Rent remains a separate cash obligation in `GameState.RentObligations`. Players
may trade assets to raise cash while rent is outstanding, but a trade does not
pay, reduce, waive, or defer rent. The engine must settle that obligation through
a distinct cash rent transaction. Nonbinding strategy plans do not authorize
future consideration or bypass validation after assets are actually acquired.

The strategy-check command now includes trade valuation, mixed assets, both
parties' fees and funding, same-turn pass-through protection, counteroffer
continuity, strict terms parsing, and preservation of rent obligations.

## Trade safety advisory

`TradeSafetyAdvisor` evaluates the strong recommendation to avoid trading away a
property whose next-roll rent would exceed the seller's entire post-trade cash
and asset value. It is separate from `TradeValidator`: a dangerous exchange can
still be legal, offered, and executed. The built-in strategies avoid it during
ordinary decision-making, while future strategies may choose differently.

The assessment uses direct movement from the player's current position with the
configured dice, including wraparound. It does not simulate Chance or Community
Chest movement, additional rolls, or future changes to buildings or mortgages.
It compares against the immediate post-trade snapshot without assuming future GO
salary or rent income. Evaluate the position and state appropriate to the next
ordinary movement roll; the engine still handles jail and movement rules.

Rent reflects the recipient's ownership **after** both sides of the exchange:
complete-color rent, house/hotel schedules, railroad counts, and utility dice
multipliers all use that projected state. A currently mortgaged transferred
property collects no rent. Each exposure reports its maximum reachable rent and
the dice totals that would exceed the player's post-trade total value. Equality
does not count as exceeding wealth.

Post-trade wealth includes cash after bank fees, retained and received properties
valued under the custom trade rules, and retained/received jail cards at $50.
Populate `PropertyState.Rent` from the property's definition and load `DiceCount`
and `DiceSides` from the rules. A reachable rent-bearing property requires its
rent schedule; missing pricing/rent inputs must not be treated as a safe trade.

`FundsDueDebt` identifies a net cash-raising exchange that takes the player from
insufficient cash to enough to cover all currently due cash obligations. The
assessment retains its jeopardy warning but clears `ShouldAvoid` for that debt
exception. Rent is listed in `RentObligations`; other currently due debts belong
in `CashObligations`, with a null payee for the bank. Do not list a rent debt in
both collections or include hypothetical future expenses.

All five built-in strategies prioritize a legal debt-funding offer over ordinary
investment preferences, even if needed to liquidate a protected target asset.
They prefer a safe sufficient offer when one is available. While a due obligation
remains recorded, they preserve the resulting cash and return control so the
engine can make the separate cash payment. A partial cash increase that cannot
cover the currently due total does not receive this exception. No advisory can
waive the two-party, valuation, funding, wash-trade, or counteroffer legality rules.

The strategy-check command includes direct-roll boundaries, wraparound, post-trade
rent calculations, assets and bank fees, the advisory/legality distinction, and
debt rescue across all five strategies.

## Simulation engine

`MonoSimEngine(Player[] players, Microsoft.Extensions.Logging.ILogger logger)`
accepts two through six players. IDs must be unique and nonempty, balances
nonnegative, positions valid, and initial properties/cards exclusively owned.
Records supply starting balances and positions; the engine copies their arrays
and derives mortgage totals from the definition. Supplied initial properties
start unimproved and unmortgaged. Player array order determines turn order.
Supported strategy identifiers are aggressive, conservative, infrastructure,
adjacent-group, blue-green, and novice; names such as `BlueGreenStrategy` are also accepted.

The .NET 10 project references the installed `Microsoft.AspNetCore.App` shared
framework to provide Microsoft logging APIs without a separate NuGet download.
The constructor accepts any compatible logger, including `NullLogger.Instance`
for simulations where output is intentionally discarded.

```csharp
using Microsoft.Extensions.Logging;
using MonoSim;
using MonoSim.Models;

using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole());
var players = new Player[]
{
    new() { Id = "a", Name = "Alice", Strategy = "aggressive", CashBalance = 1500m },
    new() { Id = "b", Name = "Bob", Strategy = "conservative", CashBalance = 1500m }
};
var engine = new MonoSimEngine(players, logs.CreateLogger<MonoSimEngine>(),
    new MonoSimEngineOptions { RandomSeed = 42, MaximumRounds = 500 });
GameState final = engine.Play();
```

Each engine runs once. `Play()` starts the loop; constructing an engine or calling
`GetState()` does not advance the simulation. `GetState()` and the final result
are defensive snapshots. Final state includes every player (including bankrupt
players), properties, decks, bank building stock, debt history, round/turn counts,
portfolio values, winner IDs, and the ending reason. The engine manages gameplay
and sends events to the supplied logger; it does not choose log files, write
outcome CSV files, or run batches of games.

### Rounds, turns, and completion

A round consists of each remaining player taking a turn in the original order;
finalized bankrupt players are skipped. `RoundNumber` counts rounds. `TurnNumber`
counts individual full player turns and remains unchanged during extra rolls.
Each turn is pre-roll, roll/movement with all mandatory debt settlement, and
post-roll. A doubles result that earns another roll gets one **combined**
post-roll/pre-roll phase; the engine calls `PostRoll` once with that phase in
the game state. It does not call both callbacks for the same interval. Three
consecutive doubles send the player to jail. Jail doubles release and move the
player without earning another roll; after the third failed attempt the cash
fine is mandatory before movement. Just visiting Jail has no imprisonment effect.

Strategies explicitly call `IPlayerTurnController.DeclareActionsComplete` after
their chosen actions. Built-in strategies do this automatically. A pre-roll
declaration permits rolling; a combined declaration permits the next roll; a
post-roll declaration ends the turn. Merely returning an action delegate without
declaring completion is an error. The next player cannot start early. Conservative
Strategy's upgrade limit continues to apply to the full turn, not each roll.

By default, the game ends with one non-bankrupt player. Optional `MaximumRounds`
is checked only between completed rounds. When reached, all remaining players
are scored using the custom trade valuations: current cash, unmortgaged property
face values plus building resale values, mortgaged principal, and $50 per held
jail-free card. Every player tied for the highest total is a shared winner.
`WinnerIds` contains all winners; `WinnerId` is populated only when unique.
`EndReason` distinguishes `LastPlayerStanding` from `RoundLimit`. Losing on
portfolio value does not mark a solvent player bankrupt. A natural last-survivor
win takes precedence if it happens during the final permitted round.

### Debt, liquidation, and bankruptcy

All cash debts are resolved in roll/movement before post-roll. Debts have a
monotonic creation sequence; cash pays older obligations first, including partial
payments. Due debts remain separate from trades, so asset exchanges never waive
rent or pay a creditor in kind. A finalized player's unpaid remainder stays in
`Debts`; active obligation arrays exclude finalized bankrupt players.

Guaranteed liquidity is cash plus building resale values, unmortgaged property
mortgage values, and $50 per held jail-free card. Already-mortgaged titles count
as zero for this calculation even though they may sell later. Debt **strictly
greater** than this sum requires a provisional bankruptcy declaration. Equality
does not. Raising enough actual cash to cover remaining debts reverses the
declaration, and completed debts are paid in age order.

The engine first offers debt-funding actions to the strategy. If necessary,
its mandatory liquidation fallback sells owned color-group buildings to the bank
at resale values, redeems held jail cards with the bank for $50 each, and uses
bank mortgages when guaranteed liquidity covers the debt. Clearing a group's
buildings before a mortgage applies to the debtor's own holdings: another owner's
buildings, possible under the custom improved-property trade rule, cannot block
the guaranteed liquidity of an owned unimproved title.

A provisionally bankrupt player can auction remaining unimproved titles, with
complete owned color groups packaged together. Buildings must be sold back to
the bank before the corresponding properties can be auctioned. No-bid assets
return to the bank for $0; returned titles become unmortgaged and available, and
returned cards go to the bottoms of their original decks. Bankruptcy is final
only when no cash, titles, buildings, or held cards remain. Bank debts follow
the same process as debts owed to players. Auctions may also be initiated during
ordinary action phases without a bankruptcy declaration using `AuctionAssets`
and an `AssetBundle`; a bundle may contain several owned properties and cards.

### Markets and execution

`IStrategyMarketPolicy` supplies bidding limits and trade acceptance outside the
engine. The default policy follows the built-in strategy families' current group
focus and reserve preferences; it may be replaced through options. As configured
for these simulations, auction bids may use available cash rather than being
capped at trade valuation for aggressive and target-focused bidding. Transfer
interest and due debts are deducted from bidder funding. Auctions use successive
binding bids in the simulation's player order, excluding the seller, finalized
bankrupt players, and provisionally bankrupt players. A struck price is committed
immediately and cannot be rescinded. Auctions have market-determined prices,
independently of ordinary trade negotiations.

The opening ask is half the bundle's auction liquidation collateral, rounded up
to a whole dollar. Collateral is the sum of each property's mortgage principal
(whether mortgaged or not), plus $50 per held jail-free card. Buildings must
already have been sold to the bank before titles enter an auction. This auction
calculation deliberately includes already mortgaged titles, as required by the
auction rules; bankruptcy's guaranteed-liquidity calculation still excludes them.

| Current offer or opening ask | Minimum increment |
| --- | --- |
| Below $100 | $10 |
| $100 through less than $500 | $25 |
| $500 through $1,000 inclusive | $50 |
| Above $1,000 | $100 |

The first bid raises the opening ask using its tier; later bids raise the actual
highest bid. Aggressive bids twice the minimum increment, capped by its available
cash. Infrastructure, Adjacent Group, and Blue-Green also double their increments
when a bundle contains a desired target. A capped bid is allowed only if it still
meets the minimum raise. Players can bid more through a custom live policy.
Passing withdraws a bidder for that auction. The current leader does not bid
against itself, and wins once all rivals withdraw. If nobody makes a legal opening
raise, assets return to the bank for $0 under the existing no-bid procedure.

Other bidders use ordinary increments and cap their bids at reference value.
They can bluff even without genuine acquisition interest when an active aggressive
or target-focused rival could still respond based on its publicly visible cash.
Bluffs are limited by reference value, available cash, transfer fees, and the
bidder's own strategy reserves. Opponents' private ceilings and reserve decisions
are not exposed. A bluff can therefore win unexpectedly, and its bidder must
complete the purchase. Target strategies keep their reserves during their own
overbids and bluffs, including Blue-Green's final-side reserve exception.

`IStrategyAuctionPolicy.NextBid` receives `AuctionState` with the bundle, reference
value, collateral, opening ask, current high bid, leader, and active bidder IDs.
It returns a binding bid or null to pass. `MaximumBid` remains available for private
acquisition-interest ceilings; custom market policies without the live interface
use ordinary minimum raises up to their supplied ceilings. The engine independently
clamps every bid to cash after outstanding debts and bank fees. Opening asks,
actual bids, passes, and struck prices are logged; private willingness ceilings
are not logged as bids.

The engine generates candidate cash purchases, cash sales, and property-for-property
swaps, checks both parties' market decisions, and revalidates before execution.
Custom strategies may submit larger legal bundles directly. `SubmitTradeOffer`
and `AcceptTradeOffer` support explicit two-party offers and property-continuous
counteroffers during a decision window. Settled negotiations close, while their
completed receipt history remains available for wash-trade protection for the
entire full turn. Market callbacks receive copied state and cannot execute
reentrant game actions. All transfers update ownership, balances, mortgage totals,
held cards, and cash ledgers. Trading an improved title keeps its buildings on
that title without changing bank stock.

### Strategy trade negotiations

For non-novice strategies, a voluntary trade must improve consolidation toward
monopolies, acquire a desired primary target, buy a mortgaged bargain, or raise
cash for a specific investment the player otherwise cannot afford. Funding
checks consider the next desired unowned property, retained mortgage redemptions,
and legal development in a completed group. A cash sale is not accepted simply
because it increases the balance. Existing cash-debt rescue remains available.
The mortgage-bargain exception applies to cash acquisitions, not exchanges that
merely swap mortgaged assets. Non-novice players also decline voluntary buybacks
of properties they sold earlier in the same full turn, preventing sale/repurchase
cycles from repeatedly claiming the same strategic benefit. Direct two-party
returns remain legal; this is a strategy preference, not a new wash-trade rule.

Players avoid giving away any property reachable on their next direct dice roll,
including mortgaged titles and destinations across GO. Conservative alone uses
two successive direct rolls in pre-roll and combined post-roll/pre-roll phases;
it uses one in post-roll or debt resolution. All other strategies use one roll
regardless of phase. Card movement is excluded. A cash-raising trade that fully
funds an existing debt can bypass this avoidance preference. These are strategy
preferences, not additional restrictions in `TradeValidator`.

Aggressive seeks consolidation and accepts singleton-for-property swaps when
they increase progress in another group. Simply adding a new singleton through
a trade is insufficient. It also seeks mortgaged bargains on every turn and
redeems/develops its holdings with available cash. It opens near reference value
and can pay up to 1.5 times reference value to complete a monopoly.

Conservative normally initiates only monopoly-completing deals, mortgage bargains,
or investment-funding sales, and makes at most one completed initiated trade per
full turn. It asks for 2 times the net reference value released and accepts at
least 1.5 times, provided the cash serves an investment need. It can counter even
when its asking price will be rejected. Mortgage purchases are capped at principal,
with bank interest paid separately. It can redeem acquired mortgages outside its
initial group using positive full-turn cash flow and improve any completed group
at most once per full turn.

Infrastructure, Adjacent Group, and Blue-Green seek their specific primary targets
with aggressive pricing. They retain target properties, including after their
trading mode changes. Once all primary target properties are owned, trading switches
to conservative bargaining immediately; hotels or contained infrastructure are not
prerequisites for that switch. Their existing purchase, development, reserve, and
jail policies otherwise continue. Owning both adjacent color groups now permits
outside mortgage bargains while primary development continues.

`MonoSimEngineOptions.TradeNegotiation` accepts `TradeNegotiationOptions` to change
`MonopolyCompletionMultiple` (1.5), `ConservativeAskingMultiple` (2), and
`ConservativeMinimumMultiple` (1.5). The asking multiple must be at least the
minimum, and all multiples must be at least one. API callers can also supply these
options directly to `DefaultStrategyMarketPolicy`.

`IStrategyTradeNegotiator` plans opening offers and counteroffers. Candidate
negotiations allow a seller counter and one buyer response. Planning identifies
preferred offers without sampling adherence. During execution, recipients can
accept, decline, or counter, applying their adherence at each actual decision.
Execution revalidates and publishes the property-continuous offer chain before
atomically settling agreed terms. A rejected proposal returns
`StrategyTurnState.ActionExecuted = false`; a completed negotiation returns its
actual terms in `ExecutedAction`, including any changed price. Actual opening
and counter terms are retained in `StrategyAction.NegotiationHistory`.

Novice purchase, mutual-monopoly probability, development, jail, and no-initiation
policies are unchanged. It does not inherit the new general consolidation,
price-multiple, or blanket property-avoidance preferences. Its existing acceptance
logic now applies to unequal exchanges made legal by the rule change, while its
financial-jeopardy check and legal funding requirements still apply.

### Logs and reproducibility

Every phase, roll, movement, card draw/return, action, cash transaction, debt,
auction, trade, bankruptcy transition, and game outcome emits an information-level
`SimulationLogEntry` through `ILogger`. It exposes standard structured logging
fields and a JSON message formatter. Entries carry a simulation ID, monotonic
sequence, round, turn, roll, phase, player ID, description, and applicable signed
amount/new cash balance. A generated or supplied random seed is logged and
returned in game state. Bank transactions are logged explicitly; bank cash is
unlimited. Configure a logger/provider that retains information-level events when
the full action history is required.

The options overload supports seeded randomness, deterministic dice, specified
initial deck orders, custom strategy factories, and custom market policies.
Dice/deck hooks support tests and replay; malformed rolls or deck orders are
rejected. In addition to the strategy and trade checks, engine tests cover full
games, phase declarations, doubles/jail, card chains, rent overrides, liquidation,
binding auctions, recovery, cash/deck/building conservation, and round-limit
scoring with shared winners.

## Novice Strategy

Use `Strategy = "novice"` to select `NoviceStrategy`. For a color group the
player does not yet own, purchase probabilities are brown 1/8, light blue 1/4,
pink 3/8, orange 1/2, red 5/8, yellow 3/4, green 7/8, and dark blue 1.
The first railroad has probability 1/4; utilities are normally declined.
Owning any property in a color group makes further purchases in that group
certain, as does owning any railroad for subsequent railroad purchases.
Blocking another player's completion of any color, railroad, or utility set
also makes a purchase certain. Every purchase must be affordable.

The novice never initiates trades. It rejects an exchange that gives the other
player a new monopoly unless it also receives a new monopoly. Legal, safe
mutual-monopoly exchanges use the absolute difference between color-group
ordinals (brown 1 through dark blue 8), divided by 8. Infrastructure pairs use
a distance factor of 1. If multiple new monopolies are completed, the highest
pairwise distance factor applies. That factor is multiplied by the probability
of avoiding all newly completed opposing monopolies on the next direct roll:
`1 - reachable monopoly spaces / possible destination spaces`. For two six-sided
dice, there are 11 equally counted destinations; dice-total weights are not used
for this trade calculation, and movement wraps past GO.

The novice rejects a combined probability below 5/8 and samples the probability
at or above 5/8. Each exchange decision is cached for the current turn and roll,
so repeated candidate and execution checks do not resample identical terms.
Existing trade validation and financial-jeopardy
checks still apply. Other legal, safe exchanges are accepted when they do not
give the counterparty a new monopoly.

Development waits until an active opponent can reach a property in a completed
group on their next direct dice roll. Among eligible groups, the novice favors
the lowest average building level, counting a hotel as five houses. It can make
multiple affordable legal improvements per turn, spreading them across groups.

Early jail release requires a directly reachable, affordable property the novice
would certainly buy, with enough cash left after any release fine. It prefers a
jail-free card to paying. It waits if the probability of landing on an opponent's
unmortgaged property exceeds 50%; exactly 50% permits release. Probabilities use
the configured dice distribution, including the unequal likelihood of dice
totals. Chance and Community Chest movement is not predicted. New jail arrivals
wait until pre-roll to consider release.

Auction interest follows the same property probabilities. Live novice bidding
uses ordinary increments with a reference-value ceiling, and it may bluff against
active susceptible rivals under the shared auction policy. Property purchase decisions are
sampled once per landing; auction decisions are cached per turn, roll, and bundle.
Engine-created novices use independent player-specific random streams derived
from the simulation seed, keeping runs reproducible without consuming dice or
deck randomness. Direct callers can supply a `Random` instance to the constructor.
`IStrategyMarketPolicy` carries these trade and auction decisions, while
`ITradeInitiationPolicy` suppresses novice-originated trade offers.

## Strategy adherence

The strategy descriptions above define expected behavior. Human decisions are
enabled by default: a player can occasionally make the opposite high-level
choice, including overriding cash reserves, target-property protection, and
rent-risk avoidance. Ownership, affordability, legal building, trade constraints,
and other game rules remain mandatory.

At the start of each batch, each player slot receives a `StrategyAdherence`
between 0.75 and 0.95, inclusive. That base value stays with the same player ID
across all simulations in the batch, independently of randomized turn order or
strategy selection. The effective probability adds the chosen strategy's fixed
`AdherenceAdjustment`:

| Strategy | Adjustment |
| --- | ---: |
| Aggressive | +0.02 |
| Conservative | +0.02 |
| Infrastructure | +0.01 |
| Adjacent Group | -0.02 |
| Blue-Green | -0.02 |
| Novice | -0.04 |

Each actual optional decision samples a value from zero to one. A sample less
than or equal to the effective probability follows the strategy; a larger
sample reverses the binary choice. Decisions include buying or passing,
improving or waiting, redeeming a mortgage, leaving jail voluntarily, proposing
a trade, accepting or countering an offer, and bidding or passing at auction.
After that choice, the strategy's normal selection determines the action's
details. If a deviation selects an action the strategy would never choose, the
engine selects a legal affordable alternative. Novice's existing purchase and
trade probabilities still determine its expected choices before adherence.

Required debt settlement, card effects, and forced actions do not sample
adherence. Neither does a decision with only one legal outcome. Passing counts
as an alternative to taking an optional action. Enumerating hypothetical trade
offers does not consume adherence samples. A failed voluntary trade ends further
proposals by that actor for the current phase, while allowing other actions.

Independent per-player random streams keep seeded batches reproducible under
parallel execution. `StrategyDecision` log events record expected and actual
choices, the sample, base probability, adjustment, and effective probability.
Result rows record both probabilities for each player, including failed games.

API-created players default to a base of 0.85; callers can supply any value in
the supported range. Custom strategies can expose `AdherenceAdjustment` (default
zero) and use `StrategyDecisions.Choose` with an `IPlayerDecisionContext` executor
to participate in optional-action decisions. Engine options
`EnableHumanDecisions = false` and `AdherenceSampler` support deterministic
strategy verification and controlled tests. The CLI enables human decisions.

## References

- [Hasbro classic rules](https://www.hasbro.com/common/instruct/Monopoly.pdf): core mechanics, supplies, mortgages, jail, and deck handling. This older rulebook is not the fixed-tax baseline.
- [U.S. property financial reference](https://www.monopolyland.com/monopoly-rent-rules/): purchase prices and rent schedules.
- [Traditional U.S. decks](https://www.monopolyland.com/list-monopoly-chance-community-chest-cards/): the 2008–2021 reference section, including the duplicate railroad card.

Card labels summarize effects rather than reproduce printed text or artwork.
