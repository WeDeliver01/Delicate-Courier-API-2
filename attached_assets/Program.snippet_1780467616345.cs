// SNIPPET: additions to Program.cs
//
// Add these registrations near the existing Shopify-related lines
// (currently lines 87-88 of Program.cs):
//
//     builder.Services.AddHttpClient<IShopifyService, ShopifyService>();
//     builder.Services.AddScoped<IShopifyService, ShopifyService>();
//
// New registrations needed:

builder.Services.AddScoped<ShopifyOrderIngestionService>();
builder.Services.AddScoped<ShopifyTrackingWriteback>();

// Note: ShopifyRatesController and ShopifyWebhookController are
// auto-discovered by [ApiController] attribute routing — no explicit
// registration needed.

// Note: ShopifyTrackingWriteback uses IHttpClientFactory which is already
// available via the existing AddHttpClient setup. No additional client
// configuration needed.
