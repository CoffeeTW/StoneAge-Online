namespace StoneAge.Game.World;

public enum MoveResult : byte
{
    Success = 0,
    NotOnline = 1,
    InvalidDirection = 2,
    TooFast = 3,
    Blocked = 4,
    InvalidTarget = 5,
    DirectionMismatch = 6
}
