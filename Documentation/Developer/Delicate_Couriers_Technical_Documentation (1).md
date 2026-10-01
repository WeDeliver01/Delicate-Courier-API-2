# Delicate Couriers Platform - Technical Documentation

**Version:** 1.0  
**Last Updated:** January 22, 2026  
**Author:** Nissi Ngandu  
**Client:** Shaun Baloyi / Delicate Couriers

---

## Table of Contents

1. [Overview](#overview)
2. [Database Connection Details](#database-connection-details)
3. [Database Schema](#database-schema)
4. [Entity Definitions](#entity-definitions)
5. [User Accounts](#user-accounts)
6. [API Configuration](#api-configuration)
7. [External Integrations](#external-integrations)

---

## Overview

The Delicate Couriers Platform is a multi-tenant SaaS logistics middleware that connects WooCommerce stores to Shiplogic's courier API. The platform automates shipment creation, label management, and tracking synchronization.

### Technology Stack

| Component | Technology |
|-----------|------------|
| Backend API | .NET 8 Web API |
| Database | PostgreSQL |
| Frontend | Next.js 15 with TypeScript |
| Authentication | JWT (JSON Web Tokens) |
| Background Jobs | Hangfire |
| Password Hashing | BCrypt |
| Hosting | Containerized (Docker) — runs on any container host |

---

## Database Connection Details

The database connection string is read from the env var `ConnectionStrings__delicatedb` at runtime. Credentials are not stored in this repo — fetch them from your secrets manager / `.env` file for the relevant environment.

### Local Development

| Property | Value |
|----------|-------|
| **Server** | `localhost` |
| **Port** | `5432` |
| **Database** | `delicatedb` |
| **Username** | `postgres` |
| **Password** | *(your local password)* |

#### Connection string template
```
Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<password>;SSL Mode=Require
```

For the Replit dev environment use `Host=helium;Port=5432;Database=heliumdb;Username=postgres;Password=password;SSL Mode=Disable` (the bundled local Postgres).

---

## Database Schema

### Entity Relationship Diagram

```
┌─────────────┐       ┌─────────────┐       ┌─────────────┐
│   Tenants   │───1:N─│   Stores    │───1:N─│   Orders    │
└─────────────┘       └─────────────┘       └─────────────┘
       │                                           │
       │                                           │1:1
       │1:N                                        │
       │                                    ┌─────────────┐
┌─────────────┐                             │  Shipments  │
│    Users    │                             └─────────────┘
└─────────────┘                                    │
                                                   │1:1
                                            ┌─────────────┐
                                            │   Labels    │
                                            └─────────────┘
                                                   │
                                                   │1:N
                                            ┌─────────────┐
                                            │ Tracking    │
                                            │   Events    │
                                            └─────────────┘
```

### Tables Summary

| Table | Description | Primary Key |
|-------|-------------|-------------|
| Tenants | Client organizations using the platform | TenantID |
| Stores | WooCommerce stores per tenant | StoreID |
| Users | User accounts with authentication | UserID |
| Orders | Orders received from WooCommerce | OrderID |
| OrderLineItems | Individual items within orders | OrderLineItemID |
| Shipments | Courier shipments created via Shiplogic | ShipmentID |
| Labels | Shipping label PDFs | LabelID |
| TrackingEvents | Shipment tracking history | TrackingEventID |

---

## Entity Definitions

### 1. Tenant

Represents a client organization using the platform.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| TenantID | int | No | Primary Key (auto-increment) |
| TenantName | varchar(100) | No | Organization name |
| TenantAPIKey | varchar(100) | No | Unique API key for tenant |
| ShiplogicBearerToken | varchar(500) | Yes | Shiplogic API token |
| IsActive | boolean | No | Whether tenant is active |
| CreatedOn | timestamp | No | Creation timestamp |
| CreatedBy | varchar(100) | No | Creator identifier |
| ChangedOn | timestamp | Yes | Last modification timestamp |
| ChangedBy | varchar(100) | Yes | Last modifier identifier |

### 2. Store

Represents a WooCommerce store connected to the platform.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| StoreID | int | No | Primary Key (auto-increment) |
| TenantID | int | No | Foreign Key to Tenants |
| StoreName | varchar(100) | No | Display name for the store |
| WooCommerceURL | varchar(500) | No | Store URL (e.g., https://store.com) |
| WooConsumerKey | varchar(200) | Yes | WooCommerce REST API consumer key |
| WooConsumerSecret | varchar(200) | Yes | WooCommerce REST API consumer secret |
| WebhookSecret | varchar(200) | Yes | Secret for webhook HMAC verification |
| IsActive | boolean | No | Whether store is active |
| **Collection Address** | | | |
| CollectionAddressLine1 | varchar(200) | Yes | Street address line 1 |
| CollectionAddressLine2 | varchar(200) | Yes | Street address line 2 |
| CollectionCity | varchar(100) | Yes | City |
| CollectionProvince | varchar(100) | Yes | Province/State |
| CollectionPostalCode | varchar(20) | Yes | Postal/ZIP code |
| CollectionCountry | varchar(10) | No | Country code (default: "ZA") |
| **Collection Contact** | | | |
| CollectionContactName | varchar(100) | Yes | Contact person name |
| CollectionContactPhone | varchar(50) | Yes | Contact phone number |
| CollectionContactEmail | varchar(200) | Yes | Contact email address |
| CollectionCompanyName | varchar(200) | Yes | Company name for collection |
| **Shiplogic Config** | | | |
| ShiplogicProviderId | int | No | Shiplogic provider ID (default: 35) |
| ShiplogicAccountId | int | No | Shiplogic account ID |
| DefaultServiceLevel | varchar(20) | No | Default service (default: "ECO") |
| **Audit Fields** | | | |
| CreatedOn | timestamp | No | Creation timestamp |
| CreatedBy | varchar(100) | No | Creator identifier |
| ChangedOn | timestamp | Yes | Last modification timestamp |
| ChangedBy | varchar(100) | Yes | Last modifier identifier |

### 3. User

Represents a user account for platform access.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| UserID | int | No | Primary Key (auto-increment) |
| TenantID | int | No | Foreign Key to Tenants |
| Email | varchar(200) | No | Login email (unique per tenant) |
| PasswordHash | varchar(500) | No | BCrypt hashed password |
| FirstName | varchar(100) | No | User's first name |
| LastName | varchar(100) | No | User's last name |
| Role | varchar(50) | No | Role: "Admin", "Manager", "User" |
| IsActive | boolean | No | Whether account is active |
| LastLoginOn | timestamp | Yes | Last login timestamp |
| CreatedOn | timestamp | No | Creation timestamp |
| CreatedBy | varchar(100) | No | Creator identifier |
| ChangedOn | timestamp | Yes | Last modification timestamp |
| ChangedBy | varchar(100) | Yes | Last modifier identifier |

### 4. Order

Represents an order received from WooCommerce.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| OrderID | int | No | Primary Key (auto-increment) |
| TenantID | int | No | Foreign Key to Tenants |
| StoreID | int | No | Foreign Key to Stores |
| WooOrderID | varchar(50) | No | WooCommerce order ID |
| WooOrderNumber | varchar(50) | No | WooCommerce order number |
| CustomerName | varchar(200) | Yes | Customer full name |
| CustomerEmail | varchar(200) | Yes | Customer email |
| CustomerPhone | varchar(50) | Yes | Customer phone |
| **Shipping Address** | | | |
| ShippingAddressLine1 | varchar(200) | Yes | Street address line 1 |
| ShippingAddressLine2 | varchar(200) | Yes | Street address line 2 |
| ShippingCity | varchar(100) | Yes | City |
| ShippingProvince | varchar(100) | Yes | Province/State |
| ShippingPostalCode | varchar(20) | Yes | Postal/ZIP code |
| ShippingCountry | varchar(10) | Yes | Country code |
| **Order Details** | | | |
| OrderTotal | decimal(18,2) | No | Total order amount |
| OrderStatus | varchar(50) | No | Status: Pending, Processing, Shipped, Delivered |
| OrderDate | timestamp | No | Original order date |
| CreatedOn | timestamp | No | Record creation timestamp |
| CreatedBy | varchar(100) | No | Creator identifier |
| ChangedOn | timestamp | Yes | Last modification timestamp |
| ChangedBy | varchar(100) | Yes | Last modifier identifier |

### 5. OrderLineItem

Represents individual items within an order.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| OrderLineItemID | int | No | Primary Key (auto-increment) |
| OrderID | int | No | Foreign Key to Orders |
| WooLineItemID | int | No | WooCommerce line item ID |
| ProductName | varchar(500) | No | Product name |
| ProductSKU | varchar(100) | Yes | Product SKU |
| WooProductID | int | No | WooCommerce product ID |
| Quantity | int | No | Quantity ordered |
| WeightPerUnit | decimal(10,3) | Yes | Weight per unit (kg) |
| TotalWeight | decimal(10,3) | Yes | Total weight (kg) |
| UnitPrice | decimal(18,2) | No | Price per unit |
| TotalPrice | decimal(18,2) | No | Total line price |

### 6. Shipment

Represents a courier shipment created via Shiplogic.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| ShipmentID | int | No | Primary Key (auto-increment) |
| OrderID | int | No | Foreign Key to Orders (unique) |
| ConsignmentID | varchar(100) | No | Shiplogic consignment ID |
| TrackingNumber | varchar(100) | No | Customer tracking number |
| CourierName | varchar(100) | No | Courier company name |
| CourierService | varchar(100) | No | Service type (Express, Economy) |
| ShipmentStatus | varchar(50) | No | Status: Created, PickedUp, InTransit, OutForDelivery, Delivered, Failed, Cancelled |
| ShippingCost | decimal(18,2) | No | Shipping cost |
| EstimatedDeliveryDate | timestamp | Yes | Estimated delivery date |
| ActualDeliveryDate | timestamp | Yes | Actual delivery date |
| CreatedOn | timestamp | No | Creation timestamp |
| CreatedBy | varchar(100) | No | Creator identifier |
| ChangedOn | timestamp | Yes | Last modification timestamp |
| ChangedBy | varchar(100) | Yes | Last modifier identifier |

### 7. Label

Represents shipping label PDFs.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| LabelID | int | No | Primary Key (auto-increment) |
| ShipmentID | int | No | Foreign Key to Shipments (unique) |
| ShiplogicShipmentId | int | No | Shiplogic's internal shipment ID |
| LabelData | bytea | No | PDF binary data |
| ContentType | varchar(50) | No | MIME type (application/pdf) |
| FileName | varchar(200) | No | Generated filename |
| FileSizeBytes | int | No | File size in bytes |
| CreatedOn | timestamp | No | Creation timestamp |

### 8. TrackingEvent

Represents shipment tracking history.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| TrackingEventID | int | No | Primary Key (auto-increment) |
| ShipmentID | int | No | Foreign Key to Shipments |
| EventDate | timestamp | No | When event occurred |
| Status | varchar(100) | No | Status description |
| Location | varchar(200) | Yes | Event location |
| Description | varchar(500) | Yes | Detailed description |
| CreatedOn | timestamp | No | Record creation timestamp |

---

## User Accounts

### Default Admin Account

| Property | Value |
|----------|-------|
| Email | *(set during registration)* |
| Role | Admin |
| Tenant | *(assigned during setup)* |

### Role Permissions

| Role | Description | Permissions |
|------|-------------|-------------|
| Admin | Full system access | All operations, user management, tenant settings |
| Manager | Operational oversight | View reports, manage orders/shipments, view analytics |
| User | Basic access | View orders, track shipments |

---

## API Configuration

### JWT Authentication

| Setting | Value |
|---------|-------|
| Issuer | DelicateCouriersAPI |
| Audience | DelicateCouriersClient |
| Expiry | 60 minutes |
| Algorithm | HS256 |

### appsettings.json Structure

```json
{
  "ConnectionStrings": {
    "delicatedb": "Host=...;Database=...;Username=...;Password=...;SSL Mode=Require"
  },
  "Jwt": {
    "SecretKey": "[STORED IN YOUR SECRETS MANAGER]",
    "Issuer": "DelicateCouriersAPI",
    "Audience": "DelicateCouriersClient",
    "ExpiryMinutes": 60
  },
  "Shiplogic": {
    "ApiBaseUrl": "https://api.shiplogic.com",
    "TimeoutSeconds": 30,
    "MaxRetryAttempts": 3,
    "CircuitBreakerFailureThreshold": 5,
    "CircuitBreakerDurationSeconds": 60,
    "RateLimitPerMinute": 60
  }
}
```

---

## External Integrations

### Shiplogic API

| Endpoint | Method | Purpose |
|----------|--------|---------|
| /v2/shipments | POST | Create shipment booking |
| /v2/shipments/{id}/label | GET | Retrieve label PDF |
| /v2/tracking/shipments | GET | Get tracking updates |
| /v2/pickups | POST | Schedule pickup |
| /v2/quotes | GET | Fetch rate estimates |
| /v2/shipments/{id}/cancel | POST | Cancel shipment |

**Base URLs:**
- Sandbox: `https://sandbox.api.shiplogic.com`
- Production: `https://api.shiplogic.com`

### WooCommerce Integration

| Endpoint | Method | Purpose |
|----------|--------|---------|
| /wp-json/wc/v3/orders | GET | Fetch orders |
| /wp-json/wc/v3/orders/{id} | GET | Get single order |

**Webhook Topics:**
- `order.created` - New order placed
- `order.updated` - Order status changed

**Webhook Verification:** HMAC-SHA256 signature validation using store's webhook secret.

---

## Deployment URLs

| Environment | API URL | Platform URL | Webhook callback URL | Mobile/app URL |
|-------------|---------|--------------|----------------------|----------------|
| Production | `https://api2.delicatecourier.co.za` | `https://app2.delicatecourier.co.za` | `https://webhooks2.delicatecourier.co.za` | `https://app2.delicatecourier.co.za` |
| Dev | `https://api2-dev.delicatecourier.co.za` | `https://app2-dev.delicatecourier.co.za` | `https://webhooks2-dev.delicatecourier.co.za` | — |
| Staging | `https://api2-staging.delicatecourier.co.za` | `https://app2-staging.delicatecourier.co.za` | `https://webhooks2-staging.delicatecourier.co.za` | — |

The API and frontend ship as Docker images (`DelicateCouriers.ApiService/Dockerfile` and `delicate-couriers-frontend/Dockerfile`) and can run on any container host with a Postgres database alongside.

---

## Troubleshooting

### Database Connection Issues

1. **Timeout errors:** Increase connection timeout to 1800 seconds.
2. **Network blocked:** Ensure your host's outbound network / firewall rules allow the Postgres port (5432).
3. **SSL errors:** Ensure SSL Mode matches what the database requires (e.g. `Require` for managed Postgres, `Disable` for local dev).

### Common SQL Queries

```sql
-- View all tenants
SELECT * FROM "Tenants";

-- View all stores for a tenant
SELECT * FROM "Stores" WHERE "TenantID" = 1;

-- View recent orders
SELECT * FROM "Orders" ORDER BY "CreatedOn" DESC LIMIT 10;

-- View shipment with order details
SELECT s.*, o."WooOrderNumber", o."CustomerName"
FROM "Shipments" s
JOIN "Orders" o ON s."OrderID" = o."OrderID"
ORDER BY s."CreatedOn" DESC;

-- Check order line items
SELECT * FROM "OrderLineItems" WHERE "OrderID" = 1;
```

---

*Document generated for Delicate Couriers Platform v1.0*
