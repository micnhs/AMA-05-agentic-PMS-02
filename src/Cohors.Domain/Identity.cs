namespace Cohors.Domain;

public static class Roles
{
    public const string Pms = "PmsSpecialist";
    public const string Medical = "MedicalSafetyReviewer";
    public const string Regulatory = "RegulatoryAffairs";
    public const string Quality = "QualityManager";
    public const string Auditor = "Auditor";
    public const string Workflow = "Workflow";
    public const string Agent = "Agent";
}

public sealed record Actor(string Id, string Name, string[] Roles, string[] Products, bool IsHuman)
{
    public void Require(params string[] roles)
    {
        if (!roles.Any(Roles.Contains))
            throw new DomainException("forbidden", "Die erforderliche Berechtigung fehlt.", 403);
    }

    public void RequireHuman(params string[] roles)
    {
        Require(roles);
        if (!IsHuman)
            throw new DomainException("human_required", "Diese Entscheidung erfordert eine menschliche Identitaet.", 403);
    }

    public void RequireProduct(string product)
    {
        if (!Products.Contains(product))
            throw new DomainException("product_scope", "Kein Zugriff auf diese Produktfamilie.", 403);
    }
}

public sealed class DomainException(string code, string message, int status = 422, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class Guard
{
    public static void Against(bool condition, string code, string message, int status = 422)
    {
        if (condition) throw new DomainException(code, message, status);
    }

    public static string Text(string? value, string field, int min = 1, int max = 16000)
    {
        var result = value?.Trim() ?? "";
        Against(result.Length < min || result.Length > max, "invalid_field",
            $"{field}: zwischen {min} und {max} Zeichen erforderlich.");
        return result;
    }
}
