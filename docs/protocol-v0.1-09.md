# StoneAge Online Protocol v0.1-09

## Battle

- `0x0801 BattleStart` server -> client
  - Int32 monsterId
  - UInt16 monsterNameLength + UTF-8 bytes
  - Int32 monsterLevel
  - Int32 playerHp
  - Int32 monsterHp
  - Int32 monsterMaxHp

- `0x0802 BattleActionRequest` client -> server
  - Byte action: `1=Attack`, `2=Defend`

- `0x0803 BattleTurnResult` server -> client
  - Byte action
  - Int32 playerDamageDealt
  - Int32 monsterDamageDealt
  - Int32 playerHp
  - Int32 monsterHp
  - Byte victory
  - Byte defeat

- `0x0804 BattleEnd` server -> client
  - Byte victory
  - Int32 expGained
  - Int32 levelsGained
  - Int32 currentLevel
  - Int64 remainingExperience

## v0.1-09 rules

- Successful map movement has a 20% random encounter check.
- Session transitions `InWorld -> InBattle -> InWorld`.
- Movement, NPC, shop, item-use and equipment handlers reject gameplay actions while the session is `InBattle`.
- Equipment attack/defense bonuses are included when a battle starts.
- Attack damage baseline: `max(1, attack - defense/2 + random[-1,+1])`.
- Defend skips the player's attack and halves incoming monster damage, minimum 1.
- Victory grants monster EXP.
- EXP needed for the next level is `currentLevel * 100` in this first implementation.
- Level-up grants +10 MaxHP, +5 MaxMP, +1 Strength, +1 Vitality, +1 Agility, +1 Endurance.
- Defeat leaves the character at 1 HP.
