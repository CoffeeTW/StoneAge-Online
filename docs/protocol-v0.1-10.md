# StoneAge Online Protocol v0.1-10

## Battle actions

`BattleActionRequest (0x0802)` payload is one byte:

- `1` Attack
- `2` Defend
- `3` Escape
- `4` Capture

## Battle result codes

`BattleEnd (0x0804)` result byte:

- `0` Defeat
- `1` Victory
- `2` Escaped
- `3` Captured

The response also includes EXP gained, level gains, current level, remaining EXP, reward id and a message.

## Element foundation

Damage uses Earth / Water / Fire / Wind affinities. The first implementation caps elemental influence between 0.75x and 1.25x.

Advantage cycle:

- Water > Fire
- Fire > Wind
- Wind > Earth
- Earth > Water

## Encounter weights

Monster definitions include `encounterWeight`. Selection is weighted after the normal encounter check.

## Drops

Monster definitions may include `dropItemId` and `dropRate`. Rewards obey normal inventory stack and 20-slot capacity rules.

## Pet capture foundation

Capturable monsters expose `captureEnabled` and `captureRate`.

Capture success is improved as monster HP decreases. Success persists a `character_pets` row with monster stats, elements and loyalty. The v0.1-10 pet limit is 5 per character.

Pet deployment, active pet battle participation, growth and skills are intentionally deferred to v0.1-11.
