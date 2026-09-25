using BullionTrading.Api.Auth;
using BullionTrading.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace BullionTrading.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        if (!await db.Users.AnyAsync(ct))
        {
            db.Users.AddRange(
                new User { Email = "admin@demo.test", DisplayName = "Demo Admin", Role = Role.Admin, PasswordHash = PasswordHasher.Hash("Admin@123") },
                new User { Email = "trader@demo.test", DisplayName = "Demo Trader", Role = Role.Trader, PasswordHash = PasswordHasher.Hash("Trader@123") });
        }

        if (!await db.Products.AnyAsync(ct))
        {
            db.Products.AddRange(
                new Product { Sku = "AU999-1G", Name = "Gold 24K 999 Coin 1 g", Metal = Metal.Gold, Purity = 999, WeightGrams = 1, BuyPremiumPct = 3.5m, SellDiscountPct = 2m },
                new Product { Sku = "AU999-10G", Name = "Gold 24K 999 Bar 10 g", Metal = Metal.Gold, Purity = 999, WeightGrams = 10, BuyPremiumPct = 2m, SellDiscountPct = 1m },
                new Product { Sku = "AU995-1KG", Name = "Gold 995 Bar 1 kg", Metal = Metal.Gold, Purity = 995, WeightGrams = 1000, BuyPremiumPct = 0.6m, SellDiscountPct = 0.3m },
                new Product { Sku = "AU916-1G", Name = "Gold 22K 916 (1 g)", Metal = Metal.Gold, Purity = 916, WeightGrams = 1, BuyPremiumPct = 3m, SellDiscountPct = 2.5m },
                new Product { Sku = "AG999-10G", Name = "Silver 999 Coin 10 g", Metal = Metal.Silver, Purity = 999, WeightGrams = 10, BuyPremiumPct = 6m, SellDiscountPct = 4m },
                new Product { Sku = "AG999-1KG", Name = "Silver 999 Bar 1 kg", Metal = Metal.Silver, Purity = 999, WeightGrams = 1000, BuyPremiumPct = 1.5m, SellDiscountPct = 1m });
        }

        await db.SaveChangesAsync(ct);
    }
}
