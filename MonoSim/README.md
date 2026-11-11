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
dotnet run
pwsh -NoProfile -File scripts/Test-GameData.ps1
dotnet build
```

The app loads the definition and prints its inventory. Build and publish outputs
include the JSON and schema; loading uses the executable's directory. Simulation,
strategies, action logging, and delimited results are subsequent work.

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
IDs, and financial ledger. Monetary fields use `decimal`. Identity, strategy,
and cash balance are required at construction; initialize cash from the game
definition's `initialState.playerCash`. Property and ledger arrays start empty.

`TotalMortgageValue` is the sum of the listed mortgage values of **all** owned
properties, including ones currently mortgaged. It is not outstanding mortgage
debt or remaining borrowing capacity. Property IDs reference board space IDs.

Each `Transaction` contains `Description`, signed `Amount` (negative for a debit,
positive for a credit), and `NewCashBalance` after the transaction. Ledger entries
are chronological. These records store snapshots; future game logic must append
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
    Strategy = "baseline",
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

`AggressiveStrategy` spends current cash only. It does not mortgage owned
properties to raise funds or keep an investment cash reserve. Its priorities are:

1. Buy the available property landed on when affordable, regardless of whether
   it completes a color group.
2. Buy affordable mortgaged properties through accepted offers from other players,
   as a secondary way to accumulate properties and eventually form monopolies.
3. Spend remaining cash on mortgage redemption and legal house/hotel upgrades,
   choosing the greatest immediate ordinary-rent increase per dollar first.

Completing a color group does not raise purchase priority. Mortgage trades never
displace an affordable landing purchase. The policy has no fixed turn-number
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

This execution interface is the integration point for the future game engine;
there is not yet a production executor, turn runner, or trade-negotiation system.
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
engine actions. The shared trade rules below determine equal values on both
sides, before mandatory bank charges. Both positive and negative net
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
player ID. The future engine must implement trade execution and current-asset
offers through `IStrategyActionExecutor`; the console app does not yet run games.

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

| Asset | Exchange value |
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

Each side's asset value plus its cash payment must exactly equal the other side.
`BankFees` is the acting player's mortgage-transfer interest;
`OtherPlayerBankFees` is the counterparty's. Each recipient owes interest on
the mortgaged properties they receive. These charges are **outside** the balanced
exchange. Both cash balances after net settlement and fees must be nonnegative.
Load `GameState.BuildingResaleFraction`, `MortgageInterestRate`, and
`GetOutOfJailFreeCardTradeValue` from the definition's corresponding rule values.

`RecordOffer` validates current ownership and funding before retaining an offer.
A counteroffer's `OriginalOfferId` must identify the root original offer, with
the same two participants. Its combined given/received properties must include
at least one property appearing on either side of that original offer. Either
or both original properties may be retained, regardless of direction. Every
counteroffer is still independently value-balanced. Cash/card-only originals
have no property that could satisfy this counteroffer requirement.

`RecordCompletedTrade` validates and records receipts without moving assets.
The future executor must atomically transfer ownership, buildings, cards, cash,
and bank fees, update both financial ledgers and total mortgage values, and
persist that receipt history in its returned state. The current test executors
exercise this contract; a production executor is still future work. Offer and
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

## References

- [Hasbro classic rules](https://www.hasbro.com/common/instruct/Monopoly.pdf): core mechanics, supplies, mortgages, jail, and deck handling. This older rulebook is not the fixed-tax baseline.
- [U.S. property financial reference](https://www.monopolyland.com/monopoly-rent-rules/): purchase prices and rent schedules.
- [Traditional U.S. decks](https://www.monopolyland.com/list-monopoly-chance-community-chest-cards/): the 2008–2021 reference section, including the duplicate railroad card.

Card labels summarize effects rather than reproduce printed text or artwork.
