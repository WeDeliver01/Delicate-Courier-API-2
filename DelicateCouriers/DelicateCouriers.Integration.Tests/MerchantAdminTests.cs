using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DelicateCouriers.Integration.Features.Admin;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// Managing merchants without a developer.
///
/// These endpoints can change where a parcel is collected from, which account a delivery is
/// billed to, and the secret a merchant's webhooks are signed with. Most of what matters here
/// is therefore about who is allowed to call them and what never comes back out.
/// </summary>
public class MerchantAdminTests : IClassFixture<MerchantAdminTests.Host>
{
    private const string AdminKey = "an-admin-key";

    private readonly Host _host;

    public MerchantAdminTests(Host host)
    {
        _host = host;
        _host.Reset();
    }

    private HttpClient Authorised()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return client;
    }

    private static MerchantUpsert NewMerchant(int storeId = 1) => new()
    {
        StoreId = storeId,
        Name = "Honey Bee Baker",
        EngineAccountRef = $"store-{storeId}",
        EngineAccountId = "11111111-1111-4111-8111-111111111111",
        ServiceLevels = new List<MerchantServiceLevel>
        {
            new()
            {
                CheckoutCode = "STD",
                CheckoutLabel = "Delicate Courier Delivery",
                EngineCode = "standard",
                EstimatedDeliveryDays = 1,
            },
        },
        DefaultPackageTypeId = Guid.Empty.ToString(),
        WooCommerceUrl = "https://honeybee.example",
        WooConsumerKey = "ck_live",
        WooConsumerSecret = "cs_live",
        Collection = new MerchantAddress
        {
            Formatted = "Honey Bee Baker, Menlyn",
            City = "Pretoria",
            Latitude = -25.7826,
            Longitude = 28.2755
        }
    };

    [Fact]
    public async Task A_collection_pin_at_null_island_is_refused_rather_than_priced()
    {
        // Not a theoretical input: the form's number fields reported "" for a half-typed
        // "-25.75", which became 0, and every merchant onboarded through it was saved at 0,0.
        // The Engine prices that happily -- a Pretoria delivery collected from the Gulf of
        // Guinea came to R101 169 instead of R211 -- so the refusal has to be here.
        var merchant = NewMerchant();
        merchant.Collection.Latitude = 0;
        merchant.Collection.Longitude = 0;

        var response = await Authorised().PostAsJsonAsync("/api/admin/merchants", merchant);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Atlantic", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_coordinate_outside_the_world_is_refused()
    {
        var merchant = NewMerchant();
        merchant.Collection.Latitude = 91;

        var response = await Authorised().PostAsJsonAsync("/api/admin/merchants", merchant);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_package_type_named_rather_than_identified_is_refused()
    {
        // "Other" is the name in the catalogue and reads like an answer. The Engine wants the
        // uuid and says only "Invalid uuid", at a shopper's checkout, as a 500 with a
        // reference -- so it is worth catching while the person who typed it is still here.
        var merchant = NewMerchant();
        merchant.DefaultPackageTypeId = "Other";

        var response = await Authorised().PostAsJsonAsync("/api/admin/merchants", merchant);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("uuid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Onboarding_a_merchant_needs_no_deploy_and_returns_their_webhook_secret()
    {
        var response = await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<MerchantCreated>();

        // The secret is generated here, not chosen: one somebody types is one somebody reuses.
        Assert.StartsWith("whk_", created!.WebhookSecret);
        Assert.Equal(1, created.Merchant.StoreId);
        Assert.True(created.Merchant.IsActive);
    }

    [Fact]
    public async Task A_new_merchant_is_servable_immediately_without_a_restart()
    {
        // The whole point of the move. The directory caches, so this proves the cache is
        // cleared on write rather than leaving the merchant invisible for half a minute.
        await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant(5));

        var merchant = _host.Directory.Find(5);
        Assert.NotNull(merchant);
        Assert.Equal("store-5", merchant!.EngineAccountRef);
    }

    [Fact]
    public async Task Secrets_never_come_back_out()
    {
        var created = await (await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant()))
            .Content.ReadFromJsonAsync<MerchantCreated>();

        var listed = await Authorised().GetStringAsync("/api/admin/merchants");

        // A console that renders these puts them in a browser, a screenshot and a support
        // ticket. It can say whether they are set; it must not say what they are.
        Assert.DoesNotContain(created!.WebhookSecret, listed);
        Assert.DoesNotContain("cs_live", listed);
        Assert.DoesNotContain("ck_live", listed);
        Assert.Contains("hasWooCredentials", listed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edit_that_omits_credentials_keeps_the_stored_ones()
    {
        // The edit form cannot render the secret, so it cannot send it back. Treating absent
        // as "blank it" would break the storefront check every time somebody fixed a typo.
        await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant());

        var edit = NewMerchant();
        edit.Name = "Honey Bee Baker (Menlyn)";
        edit.WooConsumerKey = null;
        edit.WooConsumerSecret = null;
        await Authorised().PutAsJsonAsync("/api/admin/merchants/1", edit);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
        var row = await db.Merchants.AsNoTracking().FirstAsync(m => m.StoreId == 1);

        Assert.Equal("Honey Bee Baker (Menlyn)", row.Name);
        Assert.Equal("cs_live", row.WooConsumerSecret);
    }

    [Fact]
    public async Task Rotating_a_secret_replaces_it()
    {
        var created = await (await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant()))
            .Content.ReadFromJsonAsync<MerchantCreated>();

        var rotated = await (await Authorised()
                .PostAsync("/api/admin/merchants/1/rotate-secret", null))
            .Content.ReadFromJsonAsync<MerchantCreated>();

        Assert.NotEqual(created!.WebhookSecret, rotated!.WebhookSecret);
        Assert.StartsWith("whk_", rotated.WebhookSecret);
    }

    [Fact]
    public async Task Deactivating_hides_a_merchant_without_deleting_their_history()
    {
        await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant());
        await Authorised().PostAsync("/api/admin/merchants/1/deactivate", null);

        // Gone as far as the service is concerned...
        Assert.Null(_host.Directory.Find(1));

        // ...but the row is still there, because their past orders reference it.
        var found = await Authorised().GetAsync("/api/admin/merchants/1");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
    }

    [Fact]
    public async Task Every_change_leaves_a_record()
    {
        // The settings file had no answer to "who turned this merchant off on Friday".
        await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant());
        await Authorised().PostAsync("/api/admin/merchants/1/deactivate", null);

        var changes = await Authorised().GetStringAsync("/api/admin/merchants/1/changes");
        using var json = JsonDocument.Parse(changes);
        var actions = json.RootElement.EnumerateArray()
            .Select(c => c.GetProperty("action").GetString())
            .ToList();

        Assert.Contains("merchant.create", actions);
        Assert.Contains("merchant.deactivate", actions);
    }

    [Fact]
    public async Task Refuses_a_duplicate_store_id()
    {
        await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant());
        var again = await Authorised().PostAsJsonAsync("/api/admin/merchants", NewMerchant());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ── who is allowed in ───────────────────────────────────────────────────

    [Fact]
    public async Task No_key_is_refused()
    {
        var response = await _host.CreateClient().GetAsync("/api/admin/merchants");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", "not-the-key");
        var response = await client.GetAsync("/api/admin/merchants");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_cannot_write_either()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", "not-the-key");
        var response = await client.PostAsJsonAsync("/api/admin/merchants", NewMerchant(9));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_host.Directory.Find(9));
    }

    // ── test host ───────────────────────────────────────────────────────────

    public sealed class Host : WebApplicationFactory<Program>
    {
        /// <summary>One database per test class run, thrown away afterwards.</summary>
        private readonly string _database = $"integration_test_{Guid.NewGuid():N}";

        public DbMerchantDirectory Directory { get; private set; } = null!;

        private string Connection =>
            $"Host=localhost;Port=5433;Database={_database};Username=delicate;Password=delicate";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMerchantDirectory>();
                services.RemoveAll<DbMerchantDirectory>();
                services.RemoveAll<DbContextOptions<MerchantDbContext>>();
                services.RemoveAll<AdminOptions>();

                services.AddSingleton(new AdminOptions { ApiKey = AdminKey });
                services.AddDbContext<MerchantDbContext>(o => o.UseNpgsql(Connection));
                services.AddSingleton<DbMerchantDirectory>();
                services.AddSingleton<IMerchantDirectory>(
                    sp => sp.GetRequiredService<DbMerchantDirectory>());
            });
        }

        public void Reset()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
            db.Database.EnsureCreated();
            db.MerchantChanges.ExecuteDelete();
            db.Merchants.ExecuteDelete();

            Directory = Services.GetRequiredService<DbMerchantDirectory>();
            Directory.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    using var scope = Services.CreateScope();
                    scope.ServiceProvider.GetRequiredService<MerchantDbContext>()
                        .Database.EnsureDeleted();
                }
                catch
                {
                    // A leftover test database is untidy, not harmful.
                }
            }
            base.Dispose(disposing);
        }
    }
}
