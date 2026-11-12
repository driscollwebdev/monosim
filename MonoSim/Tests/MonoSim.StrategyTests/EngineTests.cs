using Microsoft.Extensions.Logging;
using MonoSim;
using MonoSim.Models;
using MonoSim.Strategies;
using MonoSim.Trading;
using static InfrastructureStrategyTests;

internal static class EngineTests
{
    public static int RunAll()
    {
        var tests = new (string, Action)[]
        {
            ("Constructor accepts only two through six uniquely identified players", () =>
            {
                foreach (var count in new[] { 0, 1, 7 })
                {
                    Expect<ArgumentException>(() => new MonoSimEngine(Enumerable.Range(0, count).Select(i => P($"p{i}", 0)).ToArray(), new Recorder()));
                }
                Expect<ArgumentException>(() => new MonoSimEngine([P("same", 0), P("same", 0)], new Recorder()));
                Expect<ArgumentNullException>(() => new MonoSimEngine([P("a", 0), P("b", 0)], null!));
                _ = new MonoSimEngine(Enumerable.Range(0, 6).Select(i => P($"p{i}", 0)).ToArray(), new Recorder());
            }),
            ("Play returns the sole survivor and records unpaid bank debt", () =>
            {
                var log = new Recorder();
                var e = new MonoSimEngine([P("a", 50), P("b", 0)], log, Opt([1, 3]));
                var final = e.Play();
                Check(final.WinnerId == "b" && final.Phase == TurnPhase.Completed && final.Players.Count(p => !p.IsBankrupt) == 1, "No unique final winner.");
                var loser = final.Players.Single(p => p.Id == "a");
                Check(loser.IsBankrupt && loser.CashBalance == 0 && loser.OwnedPropertyIds.Length == 0 && final.Debts.Single().Remaining == 150, "Incorrect final bankruptcy or unpaid debt.");
                Check(log.Entries.Any(x => x.Event == "GameCompleted") && log.Entries.Any(x => x.Event == "BankTransaction"), "Missing completion or transaction logs.");
                Expect<InvalidOperationException>(() => e.Play());
            }),
            ("An exactly payable debt does not trigger bankruptcy and turns require declaration", () =>
            {
                var log = new Recorder();
                var e = new MonoSimEngine([P("a", 200), P("b", 0)], log, Opt([1, 3], [1, 3]));
                var final = e.Play();
                Check(final.WinnerId == "a" && !log.Entries.Any(x => x.PlayerId == "a" && x.Event == "BankruptcyDeclared"), "Equality incorrectly required bankruptcy.");
                var finish = log.Entries.FindIndex(x => x.PlayerId == "a" && x.Event == "TurnFinished");
                var next = log.Entries.FindIndex(x => x.PlayerId == "b" && x.Event == "TurnStarted");
                Check(finish >= 0 && next > finish, "The next player started before completion was declared.");
            }),
            ("Doubles produce exactly one combined post-roll/pre-roll phase", () =>
            {
                var log = new Recorder();
                var calls = new List<(string Player, TurnPhase Phase, bool Pre)>();
                var options = Opt([1, 1], [1, 2], [1, 3]) with
                {
                    StrategyFactory = (p, executor) => new Script(executor, (player, state, pre) => calls.Add((player.Id, state.Phase, pre)))
                };
                var e = new MonoSimEngine([P("a", 0), P("b", 0)], log, options);
                var final = e.Play();
                var phases = log.Entries.Where(x => x.Event == "PhaseStarted" && x.PlayerId == "a").Select(x => x.Phase).ToArray();
                Check(phases.SequenceEqual([TurnPhase.PreRoll, TurnPhase.CombinedPostRollPreRoll, TurnPhase.PostRoll]), "Duplicated or omitted an inter-roll phase.");
                Check(calls.Count(c => c.Player == "a" && c.Pre) == 1 && calls.Count(c => c.Player == "a" && !c.Pre) == 2, "Combined phase invoked both strategy callbacks.");
                Check(log.Entries.Where(x => x.PlayerId == "a" && x.Event == "DiceRolled").All(x => x.TurnNumber == 1), "A doubles roll reset the full-turn number.");
                Check(final.WinnerId == "a", "Unexpected result.");
            }),
            ("Consecutive doubles send the player directly to jail", () =>
            {
                var options = Opt([1, 1], [2, 2], [3, 3], [1, 3]) with { StrategyFactory = (_, executor) => new Script(executor) };
                var e = new MonoSimEngine([P("a", 0), P("b", 0)], new Recorder(), options);
                var final = e.Play();
                Check(final.Players.Single(p => p.Id == "a") is { IsInJail: true, BoardPosition: 10 }, "The third doubles result was moved normally.");
            }),
            ("GO and an Advance-to-GO card award salary once per crossing", () =>
            {
                var options = Opt([1, 2], [1, 3]) with { StrategyFactory = (_, executor) => new Script(executor) };
                var e = new MonoSimEngine([P("a", 0) with { BoardPosition = 39 }, P("b", 0)], new Recorder(), options);
                var final = e.Play();
                var player = final.Players.Single(p => p.Id == "a");
                Check(player.CashBalance == 400 && player.FinancialLedger.Count(t => t.Description == "GO salary") == 2, "Duplicated or omitted GO salary.");
            }),
            ("Back-three Chance resolves the chained Community Chest draw", () =>
            {
                var options = Opt([1, 2], [1, 3]) with
                {
                    StrategyFactory = (_, executor) => new Script(executor),
                    InitialDeckOrder = DeckOrder(("chance", "chance-back-three"), ("communityChest", "chest-bank-error"))
                };
                var log = new Recorder();
                var e = new MonoSimEngine([P("a", 0) with { BoardPosition = 33 }, P("b", 0)], log, options);
                var final = e.Play();
                Check(final.Players.Single(p => p.Id == "a") is { BoardPosition: 33, CashBalance: 200 }, "Did not resolve the nested card.");
                Check(log.Entries.Count(x => x.Event == "CardDrawn" && x.PlayerId == "a") == 2, "Did not log both draws.");
            }),
            ("Nearest-utility Chance uses a fresh dice roll and ten-times rent", () =>
            {
                var options = Opt([1, 2], [2, 3]) with
                {
                    StrategyFactory = (_, executor) => new Script(executor), MaximumRounds = 1,
                    InitialDeckOrder = DeckOrder(("chance", "chance-nearest-utility"))
                };
                var e = new MonoSimEngine([P("a", 100) with { BoardPosition = 33 }, P("b", 0) with { OwnedPropertyIds = ["electric-company"] }], new Recorder(), options);
                Expect<ScenarioCompleteException>(() => e.Play());
                var player = e.GetState().Players.Single(p => p.Id == "a");
                Check(player.FinancialLedger.Single(t => t.Description.Contains("Rent on electric-company")).Amount == -50, "Used the movement roll or the normal utility multiplier.");
            }),
            ("Nearest-railroad Chance charges twice the owner-count rent", () =>
            {
                var options = Opt([1, 2]) with
                {
                    StrategyFactory = (_, executor) => new Script(executor), MaximumRounds = 1,
                    InitialDeckOrder = DeckOrder(("chance", "chance-nearest-railroad-1"))
                };
                var e = new MonoSimEngine([P("a", 1000) with { BoardPosition = 4 }, P("b", 0) with { OwnedPropertyIds = ["pennsylvania-railroad", "short-line"] }], new Recorder(), options);
                Expect<ScenarioCompleteException>(() => e.Play());
                Check(e.GetState().Players.Single(p => p.Id == "a").FinancialLedger.Single(t => t.Description.Contains("Rent on pennsylvania-railroad")).Amount == -100, "Did not double two-railroad rent.");
            }),
            ("Bank redemption makes jail-card liquidity real and returns the card to its deck", () =>
            {
                var e = new MonoSimEngine([P("a", 150) with { HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] }, P("b", 0)], new Recorder(), Opt([1, 3], [1, 3]));
                var final = e.Play();
                Check(final.WinnerId == "a" && final.Decks.Single(d => d.Id == "chance").CardIds[^1] == "chance-jail-free", "Card did not return to its original deck's bottom.");
                Check(final.Players.Single(p => p.Id == "a").FinancialLedger.Any(t => t.Amount == 50 && t.Description.Contains("redemption")), "No bank redemption cash was recorded.");
            }),
            ("Already mortgaged titles contribute zero to guaranteed liquidity", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 3]) with
                {
                    StrategyFactory = (p, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (player.Id == "a" && pre)
                        {
                            executor.Execute(player.Id, state.AvailableActions.Single(a => a.Type == StrategyActionType.MortgageProperty));
                        }
                    })
                };
                var e = new MonoSimEngine([P("a", 0) with { OwnedPropertyIds = ["pennsylvania-avenue"] }, P("b", 0)], log, options);
                var final = e.Play();
                Check(log.Entries.Any(x => x.Event == "BankruptcyDeclared" && x.PlayerId == "a"), "Double-counted a mortgaged title as guaranteed liquidity.");
                Check(final.Players.Single(p => p.Id == "a").IsBankrupt, "Assets or debt remained outside final liquidation.");
            }),
            ("Auction proceeds can reverse bankruptcy and exceed reference valuation", () =>
            {
                var options = Opt([1, 3]) with { StrategyFactory = (_, executor) => new Script(executor), MaximumRounds = 1 };
                var log = new Recorder();
                var e = new MonoSimEngine([P("a", 0) with { OwnedPropertyIds = ["mediterranean-avenue", "baltic-avenue"] }, P("b", 500), P("c", 500)], log, options);
                Expect<ScenarioCompleteException>(() => e.Play());
                var a = e.GetState().Players.Single(p => p.Id == "a");
                Check(!a.IsBankrupt && !a.HasDeclaredBankruptcy && a.CashBalance == 300, "Bankruptcy did not reverse after sufficient proceeds.");
                Check(log.Entries.Any(x => x.Event == "AuctionPriceStruck" && x.Amount == 500) && log.Entries.Any(x => x.Event == "BankruptcyReversed"), "Auction was capped at trade valuation or recovery was not logged.");
            }),
            ("No-bid property packages return to the bank for zero", () =>
            {
                var log = new Recorder();
                var e = new MonoSimEngine([P("a", 0) with { OwnedPropertyIds = ["mediterranean-avenue", "baltic-avenue"] }, P("b", 0)], log, Opt([1, 3]));
                var final = e.Play();
                Check(final.Properties.Where(p => p.ColorGroup == "brown").All(p => p.IsAvailable && !p.IsMortgaged), "Unsold titles did not return to bank availability.");
                Check(log.Entries.Any(x => x.Event == "AuctionNoBids" && x.Amount == 0), "The zero-price disposition was not logged.");
            }),
            ("Multiple creditors are paid by debt age, including partial payments", () =>
            {
                foreach (var cash in new[] { 30m, 60m })
                {
                    var options = Opt([1, 2]) with { StrategyFactory = (_, executor) => new Script(executor), MaximumRounds = 1,
                        InitialDeckOrder = DeckOrder(("chance", "chance-chairman")) };
                    var e = new MonoSimEngine([P("a", cash) with { BoardPosition = 4 }, P("b", 0), P("c", 0)], new Recorder(), options);
                    Expect<ScenarioCompleteException>(() => e.Play());
                    var debt = e.GetState().Debts;
                    Check(debt.Length == 2 && debt[0].Paid == Math.Min(cash, 50) && debt[1].Paid == Math.Max(0, cash - 50), "A newer creditor received money ahead of an older debt.");
                }
            }),
            ("Finite building stock, even upgrades, and hotel rent affect real bankruptcy", () =>
            {
                var owned = Board.Where(p => p.ColorGroup is "green" or "darkBlue").Select(p => p.PropertyId).ToArray();
                var options = Opt([1, 2], [6, 6]) with
                {
                    StrategyFactory = (_, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (player.Id == "a" && pre)
                        {
                            while (true)
                            {
                                var build = state.AvailableActions.FirstOrDefault(a => a.Type is StrategyActionType.BuildHouse or StrategyActionType.BuildHotel && a.Cost <= player.CashBalance);
                                if (build is null)
                                {
                                    break;
                                }
                                var result = executor.Execute(player.Id, build);
                                player = result.Player;
                                state = result.GameState;
                            }
                        }
                    })
                };
                var e = new MonoSimEngine([P("a", 5000) with { OwnedPropertyIds = owned }, P("b", 100) with { BoardPosition = 27 }], new Recorder(), options);
                var final = e.Play();
                Check(final.BankHouses == 32 && final.BankHotels == 7 && final.Properties.Count(p => p.HotelCount == 1) == 5, "Building stock was created or lost.");
                Check(final.Debts.Single().Amount == 2000 && final.Debts.Single().Paid == 100 && final.WinnerId == "a", "Hotel rent or cash-only partial settlement was incorrect.");
            }),
            ("A player may auction a package without declaring bankruptcy", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 2], [1, 2]) with
                {
                    MaximumRounds = 1,
                    StrategyFactory = (_, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (player.Id == "a" && pre)
                        {
                            executor.Execute(player.Id, new() { Id = "voluntary-package", Type = StrategyActionType.AuctionAssets, Cost = 0,
                                Bundle = new() { PropertyIds = ["mediterranean-avenue", "baltic-avenue"] } });
                        }
                    })
                };
                var e = new MonoSimEngine([P("a", 0) with { OwnedPropertyIds = ["mediterranean-avenue", "baltic-avenue"] }, P("b", 500)], log, options);
                var final = e.Play();
                var sale = log.Entries.FindIndex(x => x.Event == "AuctionCompleted");
                Check(sale >= 0 && !log.Entries.Take(sale).Any(x => x.Event == "BankruptcyDeclared"), "Required bankruptcy before a voluntary auction.");
                Check(final.Players.Single(p => p.Id == "b").OwnedPropertyIds.Length == 2, "The struck sale was rescinded.");
            }),
            ("Strategies must declare completion before any roll can begin", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 3]) with { StrategyFactory = (_, _) => new NeverDeclares() };
                var e = new MonoSimEngine([P("a", 0), P("b", 0)], log, options);
                Expect<InvalidOperationException>(() => e.Play(), "declare completion");
                Check(!log.Entries.Any(x => x.Event == "DiceRolled"), "The roll began without a declaration.");
            }),
            ("Snapshots cannot mutate the live engine's arrays or rent schedules", () =>
            {
                var e = new MonoSimEngine([P("a", 100) with { OwnedPropertyIds = ["mediterranean-avenue"] }, P("b", 100)], new Recorder());
                var state = e.GetState();
                state.Players[0].OwnedPropertyIds[0] = "boardwalk";
                state.Properties.Single(p => p.PropertyId == "mediterranean-avenue").Rent!.Houses[0] = 99999;
                Check(e.GetState().Players[0].OwnedPropertyIds[0] == "mediterranean-avenue" && e.GetState().Properties.Single(p => p.PropertyId == "mediterranean-avenue").Rent!.Houses[0] == 10, "Snapshot changes leaked into the engine.");
            }),
            ("Jail waits for three attempts and requires cash payment before the third move", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 2], [1, 2], [1, 2], [1, 2], [1, 2], [1, 3]) with
                { StrategyFactory = (_, executor) => new Script(executor), MaximumRounds = 3 };
                var e = new MonoSimEngine([P("a", 1000) with { IsInJail = true, BoardPosition = 10 }, P("b", 1000)], log, options);
                Check(e.Play().EndReason == GameEndReason.RoundLimit, "Expected scoring after three complete rounds.");
                var a = e.GetState().Players.Single(p => p.Id == "a");
                Check(!a.IsInJail && a.BoardPosition == 13 && a.CashBalance == 950 && a.FailedJailRolls == 0, "Incorrect mandatory third-attempt release.");
                Check(log.Entries.Count(x => x.PlayerId == "a" && x.Event == "JailAttemptFailed") == 3 && a.FinancialLedger.Count(t => t.Description.Contains("jail release")) == 1, "Jail attempts or mandatory fee were duplicated.");
            }),
            ("Doubles release from jail does not grant an extra roll", () =>
            {
                var log = new Recorder();
                var options = Opt([2, 2], [1, 3]) with { StrategyFactory = (_, executor) => new Script(executor) };
                var e = new MonoSimEngine([P("a", 1000) with { IsInJail = true, BoardPosition = 10 }, P("b", 0)], log, options);
                var final = e.Play();
                Check(final.Players.Single(p => p.Id == "a") is { IsInJail: false, BoardPosition: 14 }, "Doubles did not release and move the player.");
                Check(log.Entries.Count(x => x.PlayerId == "a" && x.Event == "DiceRolled") == 1 && !log.Entries.Any(x => x.Phase == TurnPhase.CombinedPostRollPreRoll), "Jail doubles granted an extra roll.");
            }),
            ("Infrastructure Strategy uses a held jail card through the real engine", () =>
            {
                var e = new MonoSimEngine([P("a", 0) with { Strategy = "infrastructure", IsInJail = true, BoardPosition = 10,
                    HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] }, P("b", 0)], new Recorder(), Opt([1, 2], [1, 3]));
                var final = e.Play();
                Check(!final.Players.Single(p => p.Id == "a").IsInJail && final.Decks.Single(d => d.Id == "chance").CardIds[^1] == "chance-jail-free", "The real executor did not release or return the card.");
            }),
            ("Conservative Strategy retains one building upgrade across doubles phases", () =>
            {
                var e = new MonoSimEngine([P("a", 500) with { Strategy = "conservative", BoardPosition = 39,
                    OwnedPropertyIds = ["mediterranean-avenue", "baltic-avenue"] }, P("b", 0)], new Recorder(), Opt([1, 1], [2, 3], [1, 3]));
                var final = e.Play();
                Check(final.Properties.Sum(p => p.HouseCount) == 1 && final.Players.Single(p => p.Id == "a").CashBalance == 650, "The upgrade allowance reset between rolls or did not use GO income.");
            }),
            ("Real trades transfer cash and ownership and retain wash-trade history", () =>
            {
                var log = new Recorder();
                var receiptsRetainedDuringTurn = false;
                var options = Opt([1, 3]) with
                {
                    MaximumRounds = 1, MarketPolicy = new AcceptAllMarket(),
                    StrategyFactory = (_, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (player.Id == "a" && pre)
                        {
                            var trade = new PropertyTrade { OtherPlayerId = "b", PropertyIdsGiven = ["mediterranean-avenue"], PropertyIdsReceived = ["oriental-avenue"], CashPaid = 40 };
                            var engine = (MonoSimEngine)executor;
                            var root = engine.SubmitTradeOffer(player.Id, trade);
                            var counter = engine.SubmitTradeOffer("b", new() { OtherPlayerId = "a", PropertyIdsGiven = ["oriental-avenue"], PropertyIdsReceived = ["mediterranean-avenue"], CashReceived = 40, OriginalOfferId = root.Id });
                            var result = engine.AcceptTradeOffer(player.Id, counter.Id);
                            receiptsRetainedDuringTurn = result.GameState.CompletedTrades.Length == 1 && result.GameState.TradeOffers.Length == 0;
                            Check(result.Player.CashBalance == 960 && result.Player.OwnedPropertyIds.SequenceEqual(["oriental-avenue"]), "The exchange did not commit both cash and titles.");
                            var wash = new PropertyTrade { OtherPlayerId = "c", PropertyIdsGiven = ["oriental-avenue"], PropertyIdsReceived = ["vermont-avenue"] };
                            Expect<InvalidOperationException>(() => executor.Execute(player.Id, new() { Id = "wash", Type = StrategyActionType.TradeProperties, Trade = wash, Cost = 0 }));
                            Expect<InvalidOperationException>(() => engine.AcceptTradeOffer(player.Id, counter.Id));
                        }
                    })
                };
                var e = new MonoSimEngine([P("a", 1000) with { OwnedPropertyIds = ["mediterranean-avenue"] }, P("b", 1000) with { OwnedPropertyIds = ["oriental-avenue"] },
                    P("c", 1000) with { OwnedPropertyIds = ["vermont-avenue"] }], log, options);
                Expect<ScenarioCompleteException>(() => e.Play());
                var state = e.GetState();
                Check(receiptsRetainedDuringTurn && state.TradeOffers.Length == 0 && log.Entries.Any(x => x.Event == "CounterOfferMade"), "Receipt history was not retained or settled offers remained open.");
                Check(state.Players.Single(p => p.Id == "b").CashBalance == 1040, "The counterparty did not receive the cash.");
            }),
            ("Improved properties retain their buildings when traded", () =>
            {
                var options = Opt([1, 3]) with
                {
                    MaximumRounds = 1, MarketPolicy = new AcceptAllMarket(),
                    StrategyFactory = (_, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (player.Id == "a" && pre)
                        {
                            executor.Execute(player.Id, state.AvailableActions.Single(a => a.Type == StrategyActionType.BuildHouse && a.PropertyId == "mediterranean-avenue"));
                            var trade = new PropertyTrade { OtherPlayerId = "b", PropertyIdsGiven = ["mediterranean-avenue"], PropertyIdsReceived = ["oriental-avenue"], CashPaid = 15 };
                            executor.Execute(player.Id, new() { Id = "improved-exchange", Type = StrategyActionType.TradeProperties, Cost = 15, Trade = trade });
                        }
                    })
                };
                var e = new MonoSimEngine([P("a", 1000) with { OwnedPropertyIds = ["mediterranean-avenue", "baltic-avenue"] }, P("b", 1000) with { OwnedPropertyIds = ["oriental-avenue"] }], new Recorder(), options);
                Expect<ScenarioCompleteException>(() => e.Play());
                var state = e.GetState();
                Check(state.BankHouses == 31 && state.Properties.Single(p => p.PropertyId == "mediterranean-avenue").HouseCount == 1 && state.Players.Single(p => p.Id == "b").OwnedPropertyIds.Contains("mediterranean-avenue"), "Trading a building created, destroyed, or sold it to the bank.");
            }),
            ("Both sides pay mortgage-transfer interest in the real executor", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 2], [1, 2], [1, 2]) with
                {
                    MaximumRounds = 3, MarketPolicy = new AcceptAllMarket(),
                    StrategyFactory = (_, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (pre && state.TurnNumber is 1 or 2)
                        {
                            executor.Execute(player.Id, state.AvailableActions.First(a => a.Type == StrategyActionType.MortgageProperty));
                        }
                        if (pre && state.TurnNumber == 3)
                        {
                            var trade = new PropertyTrade { OtherPlayerId = "b", PropertyIdsGiven = ["mediterranean-avenue"], PropertyIdsReceived = ["baltic-avenue"], BankFees = 3, OtherPlayerBankFees = 3 };
                            executor.Execute(player.Id, new() { Id = "mortgage-swap", Type = StrategyActionType.TradeProperties, Cost = 3, Trade = trade });
                        }
                    })
                };
                var e = new MonoSimEngine([P("a", 1000) with { OwnedPropertyIds = ["mediterranean-avenue"] }, P("b", 1000) with { OwnedPropertyIds = ["baltic-avenue"] }], log, options);
                Expect<ScenarioCompleteException>(() => e.Play());
                Check(log.Entries.Count(x => x.Event == "Transaction" && x.Description == "Trade mortgage-transfer interest" && x.Amount == -3) == 2, "One recipient escaped the bank fee.");
                Check(e.GetState().Properties.Where(p => p.ColorGroup == "brown").All(p => p.IsMortgaged), "A transfer silently redeemed a mortgage.");
            }),
            ("A round limit allows every remaining player a turn and awards the highest portfolio", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 2], [2, 3]) with { MaximumRounds = 1, StrategyFactory = (_, executor) => new Script(executor) };
                var final = new MonoSimEngine([P("a", 200), P("b", 500)], log, options).Play();
                Check(final.EndReason == GameEndReason.RoundLimit && final.WinnerId == "b" && final.WinnerIds.SequenceEqual(["b"]), "Did not award the richer remaining player.");
                Check(final.RoundNumber == 1 && final.TurnNumber == 2 && final.Players.All(p => !p.IsBankrupt), "Stopped after one player or bankrupted the nonwinner.");
                Check(log.Entries.Any(e => e.Event == "RoundCompleted") && log.Entries.Any(e => e.Event == "RoundLimitReached") && !log.Entries.Any(e => e.Event == "SimulationFailed"), "The round cap did not produce a normal completed game.");
            }),
            ("Exact portfolio ties produce shared winners", () =>
            {
                var options = Opt([1, 2], [2, 3]) with { MaximumRounds = 1, StrategyFactory = (_, executor) => new Script(executor) };
                var final = new MonoSimEngine([P("a", 200), P("b", 200)], new Recorder(), options).Play();
                Check(final.WinnerId is null && final.WinnerIds.SequenceEqual(["a", "b"]), "An exact tie was broken arbitrarily.");
                Check(final.PortfolioValues.All(v => v.Total == 200), "Shared portfolio scores were not reported.");
            }),
            ("Round-limit scoring uses cash, building resale, mortgage principal, and cards", () =>
            {
                var options = Opt([1, 2], [2, 3]) with
                {
                    MaximumRounds = 1,
                    StrategyFactory = (_, executor) => new Script(executor, (player, state, pre) =>
                    {
                        if (player.Id == "a" && pre)
                        {
                            var result = executor.Execute(player.Id, state.AvailableActions.Single(a => a.Type == StrategyActionType.BuildHouse && a.PropertyId == "mediterranean-avenue"));
                            executor.Execute(player.Id, result.GameState.AvailableActions.Single(a => a.Type == StrategyActionType.MortgageProperty && a.PropertyId == "electric-company"));
                        }
                    })
                };
                var final = new MonoSimEngine([P("a", 1000) with { OwnedPropertyIds = ["mediterranean-avenue", "baltic-avenue", "electric-company"],
                    HeldGetOutOfJailFreeCardIds = ["chance-jail-free"] }, P("b", 1300)], new Recorder(), options).Play();
                var value = final.PortfolioValues.Single(v => v.PlayerId == "a");
                Check(value is { Cash: 1025, Properties: 220, Cards: 50 } && value.Total == 1295 && final.WinnerId == "b", "Used full building cost, mortgaged face value, or omitted a held card.");
            }),
            ("Extra rolls do not consume additional rounds or skip other players", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 1], [1, 2], [2, 3]) with { MaximumRounds = 1, StrategyFactory = (_, executor) => new Script(executor) };
                var final = new MonoSimEngine([P("a", 100), P("b", 400)], log, options).Play();
                Check(final.RoundNumber == 1 && final.TurnNumber == 2 && log.Entries.Count(e => e.PlayerId == "a" && e.Event == "DiceRolled") == 2 &&
                    log.Entries.Any(e => e.PlayerId == "b" && e.Event == "TurnFinished"), "Doubles shortened the round or stopped before its completion.");
            }),
            ("Eliminated players cannot share a round-limit win even at equal zero value", () =>
            {
                var options = Opt([1, 3], [1, 2], [2, 3]) with { MaximumRounds = 1, StrategyFactory = (_, executor) => new Script(executor) };
                var final = new MonoSimEngine([P("a", 0), P("b", 0), P("c", 0)], new Recorder(), options).Play();
                Check(final.WinnerIds.SequenceEqual(["b", "c"]) && final.Players.Single(p => p.Id == "a").IsBankrupt, "Included a finalized bankrupt player in the ranking.");
            }),
            ("Last-player-standing completion takes precedence over the round cap", () =>
            {
                var options = Opt([1, 3]) with { MaximumRounds = 1 };
                var final = new MonoSimEngine([P("a", 0), P("b", 0)], new Recorder(), options).Play();
                Check(final.EndReason == GameEndReason.LastPlayerStanding && final.WinnerIds.SequenceEqual(["b"]) && final.TurnNumber == 1, "Forced the last survivor to take an unnecessary turn or used portfolio scoring.");
            }),
            ("Two configured rounds give each surviving player two turns", () =>
            {
                var log = new Recorder();
                var options = Opt([1, 2], [1, 2], [2, 3], [2, 3]) with { MaximumRounds = 2, StrategyFactory = (_, executor) => new Script(executor) };
                var final = new MonoSimEngine([P("a", 200), P("b", 200)], log, options).Play();
                Check(final.RoundNumber == 2 && final.TurnNumber == 4 && log.Entries.Count(e => e.Event == "RoundStarted") == 2 &&
                    log.Entries.Count(e => e.Event == "TurnStarted" && e.PlayerId == "a") == 2 && log.Entries.Count(e => e.Event == "TurnStarted" && e.PlayerId == "b") == 2,
                    "The cap counted individual player turns instead of rounds.");
            }),
            ("Round limits must be positive when supplied", () =>
            {
                foreach (var maximum in new[] { 0L, -1L })
                {
                    Expect<ArgumentOutOfRangeException>(() => new MonoSimEngine([P("a", 0), P("b", 0)], new Recorder(), new() { MaximumRounds = maximum }));
                }
            }),
            ("A seeded full simulation preserves ledger, ownership, stock, and deck invariants", () =>
            {
                var log = new Recorder();
                var e = new MonoSimEngine([P("a", 1500), P("b", 1500) with { Strategy = "conservative" }], log,
                    new MonoSimEngineOptions { RandomSeed = 42, MaximumRounds = 10000 });
                var final = e.Play();
                Check(final.Players.Count(p => !p.IsBankrupt) == 1 && final.WinnerId is not null, "The seeded game did not end in a genuine winner.");
                foreach (var p in final.Players)
                {
                    Check(1500 + p.FinancialLedger.Sum(t => t.Amount) == p.CashBalance, "Ledger cash did not reconcile.");
                    Check(p.TotalMortgageValue == p.OwnedPropertyIds.Sum(id => final.Properties.Single(g => g.PropertyId == id).MortgageValue), "Mortgage totals drifted.");
                    if (p.IsBankrupt)
                    {
                        Check(p.CashBalance == 0 && p.OwnedPropertyIds.Length + p.HeldGetOutOfJailFreeCardIds.Length == 0, "A finalized bankrupt player retained assets.");
                    }
                }
                var titles = final.Players.SelectMany(p => p.OwnedPropertyIds).ToArray();
                Check(titles.Distinct().Count() == titles.Length && final.Properties.All(p => p.IsOwned == titles.Contains(p.PropertyId)), "Property ownership diverged.");
                Check(final.BankHouses + final.Properties.Sum(p => p.HouseCount) == 32 && final.BankHotels + final.Properties.Sum(p => p.HotelCount) == 12, "Bank stock was not conserved.");
                var cards = final.Decks.SelectMany(d => d.CardIds).Concat(final.Players.SelectMany(p => p.HeldGetOutOfJailFreeCardIds)).ToArray();
                Check(cards.Length == 32 && cards.Distinct().Count() == 32, "Deck cards were lost or duplicated.");
                Check(log.Entries.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, log.Entries.Count).Select(i => (long)i)) &&
                    log.Entries.All(x => x.SimulationId == final.SimulationId), "Log sequences or simulation identifiers were inconsistent.");
            })
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS Engine: {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Engine: {name}: {error}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} engine checks passed.");
        return failures;
    }

    private static Player P(string id, decimal cash) => new() { Id = id, Name = id, Strategy = "aggressive", CashBalance = cash };
    private static MonoSimEngineOptions Opt(params int[][] dice)
    {
        var queue = new Queue<int[]>(dice);
        return new() { RandomSeed = 1234, ShuffleDecks = false, DiceRoller = () =>
        {
            if (queue.Count == 0)
            {
                throw new ScenarioCompleteException();
            }
            return queue.Dequeue();
        } };
    }
    private static Dictionary<string, string[]> DeckOrder(params (string Deck, string First)[] choices)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "monopoly-us-classic.json")));
        return choices.ToDictionary(c => c.Deck, c => new[] { c.First }.Concat(doc.RootElement.GetProperty("decks").EnumerateArray().Single(d => d.GetProperty("id").GetString() == c.Deck)
            .GetProperty("cards").EnumerateArray().Select(card => card.GetProperty("id").GetString()!).Where(id => id != c.First)).ToArray());
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
    private static void Expect<T>(Action action, string? text = null) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            if (text is null || error.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw;
        }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class Script(IStrategyActionExecutor executor, Action<Player, GameState, bool>? act = null) : IPlayingStrategy
    {
        public Action PreRoll(Player p, GameState s) => () => Run(p, s, true);
        public Action PostRoll(Player p, GameState s) => () => Run(p, s, false);
        private void Run(Player p, GameState s, bool pre)
        {
            act?.Invoke(p, s, pre);
            ((IPlayerTurnController)executor).DeclareActionsComplete(p.Id);
        }
    }
    private sealed class NeverDeclares : IPlayingStrategy
    {
        public Action PreRoll(Player p, GameState s) => () => { };
        public Action PostRoll(Player p, GameState s) => () => { };
    }
    private sealed class ScenarioCompleteException : Exception;
    private sealed class Recorder : ILogger
    {
        internal readonly List<SimulationLogEntry> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is SimulationLogEntry entry)
            {
                Entries.Add(entry);
            }
        }
    }

    private sealed class AcceptAllMarket : IStrategyMarketPolicy
    {
        public decimal MaximumBid(Player player, GameState state, AssetBundle bundle, decimal valuation, decimal bankFees) => 0;
        public bool AcceptTrade(Player player, GameState state, PropertyTrade trade) => true;
    }
}
