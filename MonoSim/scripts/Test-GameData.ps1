#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$dataDirectory = Join-Path $PSScriptRoot '../Data'
$json = Get-Content -LiteralPath (Join-Path $dataDirectory 'monopoly-us-classic.json') -Raw
if (-not (Test-Json -Json $json -SchemaFile (Join-Path $dataDirectory 'monopoly.schema.json'))) {
    throw 'Game definition does not match its schema.'
}
$game = $json | ConvertFrom-Json

function Assert-Data([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$board = @($game.board)
Assert-Data (($board.position -join ',') -eq ((0..39) -join ',')) 'Board must be ordered at positions 0 through 39.'
Assert-Data (@($board.id | Select-Object -Unique).Count -eq 40) 'Space IDs must be unique.'
$spaces = @{}
foreach ($space in $board) { $spaces[$space.id] = $space }
$expectedTypes = @{ street = 22; railroad = 4; utility = 2; drawCard = 6; tax = 2; go = 1; jail = 1; freeParking = 1; goToJail = 1 }
foreach ($type in $expectedTypes.Keys) {
    Assert-Data (@($board | Where-Object type -EQ $type).Count -eq $expectedTypes[$type]) "Incorrect number of $type spaces."
}
$expectedGroups = @{ brown = 2; lightBlue = 3; pink = 3; orange = 3; red = 3; yellow = 3; green = 3; darkBlue = 2 }
foreach ($group in $expectedGroups.Keys) {
    Assert-Data (@($board | Where-Object colorGroup -EQ $group).Count -eq $expectedGroups[$group]) "Incorrect size of $group group."
}
foreach ($space in $board | Where-Object type -In @('street', 'railroad', 'utility')) {
    Assert-Data ($space.mortgageValue * 2 -eq $space.price) "Incorrect mortgage for $($space.id)."
    if ($space.type -eq 'street') {
        Assert-Data ($space.rent.completeColorGroup -eq 2 * $space.rent.base) "Incorrect complete-group rent for $($space.id)."
        $schedule = @($space.rent.base) + @($space.rent.houses) + @($space.rent.hotel)
        for ($i = 1; $i -lt $schedule.Count; $i++) {
            Assert-Data ($schedule[$i] -gt $schedule[$i - 1]) "Rent must increase with development for $($space.id)."
        }
        $expectedBuildingPrice = 50 * (1 + [int][Math]::Floor($space.position / 10))
        Assert-Data ($space.housePrice -eq $expectedBuildingPrice -and $space.hotelPrice -eq $expectedBuildingPrice) "Incorrect building price for $($space.id)."
    } elseif ($space.type -eq 'railroad') {
        Assert-Data (($space.rent.byRailroadsOwned -join ',') -eq '25,50,100,200') 'Incorrect railroad rent.'
    } else {
        Assert-Data (($space.rent.diceMultipliersByUtilitiesOwned -join ',') -eq '4,10') 'Incorrect utility rent.'
    }
}

Assert-Data ((($game.decks.id | Sort-Object) -join ',') -eq 'chance,communityChest') 'Each deck must be defined once.'
$cards = @($game.decks | ForEach-Object { $_.cards })
Assert-Data (@($cards.id | Select-Object -Unique).Count -eq 32) 'Physical card IDs must be globally unique.'
foreach ($space in $board | Where-Object type -EQ 'drawCard') {
    Assert-Data ($space.deckId -in $game.decks.id) "Unresolved deck reference on $($space.id)."
}
foreach ($deck in $game.decks) {
    Assert-Data (@($deck.cards | Where-Object { $_.effects.type -contains 'retain' }).Count -eq 1) "Expected one jail-release card in $($deck.id)."
}
$effects = @($cards | ForEach-Object { $_.effects }) + @($board | Where-Object effects | ForEach-Object { $_.effects })
foreach ($effect in $effects) {
    if ($effect.type -in @('moveTo', 'goToJail')) {
        Assert-Data ($spaces.ContainsKey($effect.destinationId)) "Unresolved destination $($effect.destinationId)."
    }
    if ($effect.type -eq 'goToJail') {
        Assert-Data ($spaces[$effect.destinationId].type -eq 'jail') 'Jail effects must target Jail.'
    }
    if ($effect.type -eq 'transfer') {
        Assert-Data ($effect.from -ne $effect.to -and 'currentPlayer' -in @($effect.from, $effect.to)) 'Transfers must involve distinct parties including the current player.'
    }
}
$railroadCards = @($cards | Where-Object { $_.effects.type -contains 'moveToNearest' -and $_.effects.spaceType -contains 'railroad' })
Assert-Data ($railroadCards.Count -eq 2) 'Do not deduplicate the nearest-railroad cards.'
foreach ($card in $railroadCards) {
    Assert-Data ($card.effects[0].rentOverride.type -eq 'multiplyNormalRent' -and $card.effects[0].rentOverride.multiplier -eq 2) 'Railroad cards double rent.'
}
$utility = ($cards | Where-Object id -EQ 'chance-nearest-utility').effects[0]
Assert-Data ($utility.rentOverride.type -eq 'diceMultiplier' -and $utility.rentOverride.multiplier -eq 10 -and $utility.rentOverride.rollNewDice) 'Utility card requires a fresh roll at 10 times the total.'
$back = ($cards | Where-Object id -EQ 'chance-back-three').effects[0]
Assert-Data ($back.spaces -eq -3 -and -not $back.collectGo -and $back.resolveDestination) 'Back-three must resolve its destination without GO salary.'
Assert-Data ($board[36 + $back.spaces].deckId -eq 'communityChest') 'Back-three from Chance 36 must trigger Community Chest 33.'
$birthday = ($cards | Where-Object id -EQ 'chest-birthday').effects[0]
Assert-Data ($birthday.from -eq 'eachOtherPlayer' -and $birthday.to -eq 'currentPlayer' -and $birthday.amount -eq 10) 'Birthday pays 10 from each opponent.'
$chairman = ($cards | Where-Object id -EQ 'chance-chairman').effects[0]
Assert-Data ($chairman.from -eq 'currentPlayer' -and $chairman.to -eq 'eachOtherPlayer' -and $chairman.amount -eq 50) 'Chairman pays 50 to each opponent.'
Assert-Data ($spaces['boardwalk'].rent.hotel -eq 2000 -and $spaces['mediterranean-avenue'].rent.base -eq 2) 'Incorrect boundary street rents.'
Assert-Data ($spaces['income-tax'].effects[0].amount -eq 200 -and $spaces['luxury-tax'].effects[0].amount -eq 100) 'Fixed taxes must be 200/100.'
Assert-Data ($game.initialState.playerCash -eq 1500 -and $game.initialState.bankHouses -eq 32 -and $game.initialState.bankHotels -eq 12) 'Incorrect initial cash or building supply.'
Assert-Data ($spaces.ContainsKey($game.initialState.playerPositionId)) 'Initial player position must exist.'
Write-Output 'Game data schema and integrity checks passed: 40 spaces, 28 properties, 32 cards.'
