using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Data;

public partial class FloraDbContext
{
    public IHttpContextAccessor? HttpContextAccessor { get; set; }
    public bool IsSeller => HttpContextAccessor?.HttpContext?.User.IsInRole("SELLER") == true;
    public Guid? TenantId => Guid.TryParse(HttpContextAccessor?.HttpContext?.User.FindFirstValue("seller_id"), out var id) ? id : null;
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entities.Payment>().Property(x => x.PaidBy).HasColumnName("paid_by");
        modelBuilder.Entity<Entities.WithdrawalRequest>().Property(x => x.PaidBy).HasColumnName("paid_by");
        modelBuilder.Entity<Entities.WithdrawalRequest>().Property(x => x.ApprovedAt).HasColumnName("approved_at");
        // EF parameterizes context properties for each request, never a captured tenant value.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var property = entity.FindProperty("SellerId") ?? entity.FindProperty("CurrentSellerId");
            if (entity.ClrType == typeof(Entities.Seller)) property = entity.FindProperty("Id");
            if (property is null) continue;
            var row = Expression.Parameter(entity.ClrType, "row");
            var actual = Expression.Convert(Expression.Property(row, property.Name), typeof(Guid?));
            var context = Expression.Constant(this);
            var body = Expression.OrElse(Expression.Not(Expression.Property(context, nameof(IsSeller))),
                Expression.Equal(actual, Expression.Property(context, nameof(TenantId))));
            modelBuilder.Entity(entity.ClrType).HasQueryFilter(Expression.Lambda(body, row));
        }
    }
}
