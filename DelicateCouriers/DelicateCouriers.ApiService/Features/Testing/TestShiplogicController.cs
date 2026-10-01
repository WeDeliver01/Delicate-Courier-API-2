using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Features.Shiplogic.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Features.Testing;

[ApiController]
[Route("api/testing/shiplogic")]
[Authorize]
public class TestShiplogicController : ControllerBase
{
    private readonly IShiplogicService _shiplogicService;
    private readonly AppDbContext _context;
    private readonly ILogger<TestShiplogicController> _logger;

    public TestShiplogicController(IShiplogicService shiplogicService, AppDbContext context, ILogger<TestShiplogicController> logger)
    {
        _shiplogicService = shiplogicService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Test Get Rates endpoint with real Shiplogic API call
    /// </summary>
    [HttpPost("test-rates/{storeId}")]
    public async Task<ActionResult<object>> TestGetRates(int storeId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Testing Get Rates for StoreID: {StoreId}", storeId);

        try
        {
            // Fetch store with tenant
            var store = await _context.Stores.Include(s => s.Tenant).FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive, cancellationToken);

            if (store == null)
            {
                return NotFound(new { success = false, error = "Store not found or inactive" });
            }

            if (string.IsNullOrEmpty(store.Tenant.ShiplogicBearerToken))
            {
                return BadRequest(new { success = false, error = "Shiplogic bearer token not configured" });
            }

            // Build test collection address from store
            var collectionAddress = new AddressDto
            {
                Type = "business",
                Company = store.CollectionCompanyName ?? store.StoreName,
                Street = store.CollectionAddressLine1 ?? "",
                Suburb = store.CollectionAddressLine2 ?? "",
                City = store.CollectionCity ?? "",
                PostalCode = store.CollectionPostalCode ?? "",
                Province = store.CollectionProvince ?? "",
                Country = "ZA",
                Contact = new ContactDto
                {
                    Name = store.CollectionContactName ?? "Test Contact",
                    Mobile = store.CollectionContactPhone ?? "",
                    Email = store.CollectionContactEmail
                }
            };

            // Build test delivery address (Mooikloof - known working address)
            var deliveryAddress = new AddressDto
            {
                Type = "residential",
                Company = "Test Customer",
                Street = "691 Blesbok Avenue",
                Suburb = "Mooikloof",
                City = "Pretoria",
                PostalCode = "0081",
                Province = "Gauteng",
                Country = "ZA",
                Contact = new ContactDto
                {
                    Name = "Test Customer",
                    Mobile = "+27123456789",
                    Email = "test@example.com"
                }
            };

            // Build test parcel
            var parcels = new List<ParcelDto>
            {
                new ParcelDto
                {
                    Description = "Test Package",
                    Length = 30,
                    Width = 20,
                    Height = 10,
                    Weight = 2.5m
                }
            };

            // Call real Shiplogic API
            var startTime = DateTime.UtcNow;
            var response = await _shiplogicService.GetRatesAsync(store.Tenant.ShiplogicBearerToken,
                                                                 collectionAddress,
                                                                 deliveryAddress,
                                                                 parcels,
                                                                 store.DefaultServiceLevel,
                                                                 cancellationToken
                                                             );

            var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger.LogInformation("Get Rates test completed. Rates count: {Count}, Response time: {Ms}ms", response.Rates.Count, responseTime);

            return Ok(new
            {
                success = true,
                endpoint = "POST /rates",
                responseTime = $"{responseTime}ms",
                ratesCount = response.Rates.Count,
                rates = response.Rates.Select(r => new
                {
                    serviceLevelId = r.ServiceLevelId,
                    serviceLevelCode = r.ServiceLevelCode,
                    serviceLevelName = r.ServiceLevelName,
                    cost = r.Rate,
                    estimatedDeliveryDays = r.EstimatedDeliveryDays
                }).ToList(),
                testData = new
                {
                    collectionAddress = new
                    {
                        city = collectionAddress.City,
                        postalCode = collectionAddress.PostalCode
                    },
                    deliveryAddress = new
                    {
                        street = deliveryAddress.Street,
                        city = deliveryAddress.City,
                        postalCode = deliveryAddress.PostalCode
                    },
                    parcel = new
                    {
                        dimensions = $"{parcels[0].Length}x{parcels[0].Width}x{parcels[0].Height}cm",
                        weight = $"{parcels[0].Weight}kg"
                    }
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Shiplogic API error testing rates for StoreID: {StoreId}", storeId);

            return BadRequest(new
            {
                success = false,
                endpoint = "POST /rates",
                error = ex.Message,
                innerError = ex.InnerException?.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error testing rates for StoreID: {StoreId}", storeId);

            return StatusCode(500, new
            {
                success = false,
                error = "An unexpected error occurred",
                details = ex.Message
            });
        }
    }
}