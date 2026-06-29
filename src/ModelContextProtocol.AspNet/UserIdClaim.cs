namespace ModelContextProtocol.AspNet;

internal readonly struct UserIdClaim : IEquatable<UserIdClaim>
{
    private readonly string _type;
    private readonly string _value;
    private readonly string _issuer;

    public UserIdClaim(string type, string value, string issuer)
    {
        _type = type;
        _value = value;
        _issuer = issuer;
    }

    public bool Equals(UserIdClaim other) =>
        string.Equals(_type, other._type, StringComparison.Ordinal) &&
        string.Equals(_value, other._value, StringComparison.Ordinal) &&
        string.Equals(_issuer, other._issuer, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is UserIdClaim other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_type);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_value);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_issuer);
            return hash;
        }
    }

    public static bool operator ==(UserIdClaim left, UserIdClaim right) => left.Equals(right);

    public static bool operator !=(UserIdClaim left, UserIdClaim right) => !left.Equals(right);
}
