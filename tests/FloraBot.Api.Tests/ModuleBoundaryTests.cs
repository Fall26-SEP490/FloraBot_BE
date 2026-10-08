using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Tests;

public sealed class ModuleBoundaryTests
{
    private static readonly Dictionary<string, string> Schemas = new()
    {
        ["Identity"] = "identity",
        ["Catalog"] = "catalog",
        ["KioskOps"] = "kiosk_ops",
        ["Ordering"] = "ordering",
        ["Payment"] = "payment",
        ["Notify"] = "notify",
        ["Ai"] = "ai",
        ["ReadModels"] = "screen",
        ["ReadRouting"] = "routing",
        ["ResourceRouting"] = "routing",
        ["Auth"] = "identity",
        ["Jobs"] = "routing",
        ["Realtime"] = "routing"
    };
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(x => x.FieldType == typeof(OpCode)).Select(x => (OpCode)x.GetValue(null)!)
        .ToDictionary(x => unchecked((ushort)x.Value));
    private static readonly Regex TableReference = new("\\b(?:FROM|JOIN|UPDATE|INTO|TABLE)\\s+\"?(identity|catalog|kiosk_ops|ordering|payment|notify|ai|screen)\"?\\s*\\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void ModulesReferenceOnlyTheirOwnEntitiesAndSqlTables()
    {
        using var db = new FloraDbContext(new DbContextOptionsBuilder<FloraDbContext>().UseNpgsql("Host=unused;Database=unused;Username=unused").Options);
        var entities = db.Model.GetEntityTypes().ToDictionary(x => x.ClrType, x => x.GetViewSchema() ?? x.GetSchema()!);
        Assert.All(entities.Values, schema => Assert.Contains(schema, Schemas.Values));
        var violations = new HashSet<string>();
        foreach (var type in typeof(Program).Assembly.GetTypes().Where(x => Owner(x) is not null))
        {
            var owner = Owner(type)!;
            Assert.True(Schemas.TryGetValue(owner, out var schema), $"Unmapped module {owner}");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                CheckType(field.FieldType, schema!, entities, $"{type.FullName}.{field.Name}", violations);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            {
                var label = $"{type.FullName}.{method.Name}";
                foreach (var referenced in SignatureTypes(method)) CheckType(referenced, schema!, entities, label, violations);
                var body = method.GetMethodBody();
                if (body?.GetILAsByteArray() is not { } il) continue;
                foreach (var local in body.LocalVariables) CheckType(local.LocalType, schema!, entities, label, violations);
                var offset = 0;
                while (offset < il.Length)
                {
                    ushort code = il[offset++];
                    if (code == 0xfe) code = (ushort)(0xfe00 | il[offset++]);
                    var operand = Codes[code].OperandType;
                    if (operand == OperandType.InlineString)
                    {
                        var sql = method.Module.ResolveString(BitConverter.ToInt32(il, offset));
                        if (owner == "ResourceRouting" && Regex.IsMatch(sql, @"\b(identity|catalog|kiosk_ops|ordering|payment|notify|ai|screen)\s*\.", RegexOptions.IgnoreCase))
                            violations.Add($"{label}: authorization orchestration must delegate schema access");
                        foreach (var foreign in ForeignSchemas(sql, schema!)) violations.Add($"{label}: SQL reads/writes {foreign}");
                        if (owner == "ReadModels" && Regex.IsMatch(sql, @"\b(INSERT|UPDATE|DELETE|TRUNCATE|ALTER|DROP|CREATE)\b", RegexOptions.IgnoreCase))
                            violations.Add($"{label}: read models must not execute mutations");
                    }
                    else if (operand is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                    {
                        var member = method.Module.ResolveMember(BitConverter.ToInt32(il, offset), type.GetGenericArguments(), method is MethodInfo mi ? mi.GetGenericArguments() : null)!;
                        foreach (var referenced in SignatureTypes(member)) CheckType(referenced, schema!, entities, label, violations);
                    }
                    offset += operand switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                        _ => 4
                    };
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Order()));
    }

    [Fact]
    public void GuardDetectsForeignSqlAndGenericEntityAccess()
    {
        Assert.Equal(["ordering"], ForeignSchemas("SELECT * FROM payment.payments p JOIN \"ordering\".orders o ON true", "payment"));
        Assert.Empty(ForeignSchemas("SELECT flow.check_catalog_editor(@id,@user)", "catalog"));
        Assert.Equal(["screen"], ForeignSchemas("SELECT * FROM screen.v_kiosk_catalog", "catalog"));
        Assert.Empty(ForeignSchemas("SELECT * FROM screen.v_kiosk_catalog", "screen"));
        Assert.Equal(["identity"], ForeignSchemas("SELECT * FROM identity.sellers", "screen"));
        var violations = new HashSet<string>();
        CheckType(typeof(Task<List<Order>>), "payment", new Dictionary<Type, string> { [typeof(Order)] = "ordering" }, "fixture", violations);
        Assert.Single(violations);
        violations.Clear();
        CheckType(typeof(DbSet<Order>), "ordering", new Dictionary<Type, string> { [typeof(Order)] = "ordering" }, "fixture", violations);
        Assert.Empty(violations);
    }

    private static string? Owner(Type type)
    {
        const string prefix = "FloraBot.Api.Modules.";
        if (type.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true) return type.Namespace[prefix.Length..].Split('.')[0];
        if (type.Namespace == "FloraBot.Api.ReadModels") return "ReadModels";
        if (type.Namespace == "FloraBot.Api.Auth") return "Auth";
        if (type.Namespace == "FloraBot.Api.Jobs") return "Jobs";
        if (type.Namespace == "FloraBot.Api.Realtime") return "Realtime";
        if (type.FullName == "FloraBot.Api.Infrastructure.ReadEndpoints" || type.FullName?.StartsWith("FloraBot.Api.Infrastructure.ReadEndpoints+", StringComparison.Ordinal) == true) return "ReadRouting";
        if (type.FullName == "FloraBot.Api.Infrastructure.ResourceAccess" || type.FullName?.StartsWith("FloraBot.Api.Infrastructure.ResourceAccess+", StringComparison.Ordinal) == true) return "ResourceRouting";
        return null;
    }

    private static IEnumerable<string> ForeignSchemas(string sql, string own) => TableReference.Matches(sql)
        .Select(x => x.Groups[1].Value.ToLowerInvariant()).Where(x => x != own).Distinct();

    private static void CheckType(Type type, string schema, IReadOnlyDictionary<Type, string> entities, string label, ISet<string> violations)
    {
        if (entities.TryGetValue(type, out var owner) && owner != schema) violations.Add($"{label}: entity {type.Name} belongs to {owner}");
        if (type.HasElementType) CheckType(type.GetElementType()!, schema, entities, label, violations);
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments()) CheckType(argument, schema, entities, label, violations);
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member)
    {
        if (member is Type type) yield return type;
        if (member.DeclaringType is { } declaring) yield return declaring;
        if (member is FieldInfo field) yield return field.FieldType;
        if (member is MethodInfo method)
        {
            yield return method.ReturnType;
            foreach (var argument in method.GetGenericArguments()) yield return argument;
        }
        if (member is MethodBase callable)
            foreach (var parameter in callable.GetParameters()) yield return parameter.ParameterType;
    }
}
