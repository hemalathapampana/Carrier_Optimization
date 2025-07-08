# Sync Strategy

## Overview

This document outlines the three critical sync strategy rules that govern device data synchronization in the Altaworx SIM Card Cost Optimization system. These strategies ensure efficient data management, transaction isolation, and optimal performance across different synchronization scenarios.

---

## 1. Full Sync: Triggered for new sessions, clears staging tables, syncs last 30+ days

### What
Executes a complete device synchronization that clears all staging tables and retrieves device data from the last 30+ days for new optimization sessions

### Why
Ensures data freshness and completeness for new optimization sessions by starting with a clean slate, eliminating potential data inconsistencies from previous runs and providing comprehensive device information for accurate optimization decisions

### How
Checks for HasSynced flag in SQS message attributes, truncates staging tables when false, and initiates device sync with a calculated date range of last month plus one day

### Algorithm
```
ALGORITHM: ExecuteFullSync
INPUT: SQS Message Attributes, Service Provider ID, Optimization Session ID
OUTPUT: Complete Device Synchronization

Step 1: Check Synchronization Status
       Examine message.MessageAttributes for "HasSynced" key
       If "HasSynced" key is missing or cannot be parsed as boolean
       Then set hasSynced = false (default to full sync)
       If hasSynced is already true, skip full sync process

Step 2: Validate Full Sync Requirement
       If hasSynced equals false
       Then proceed with full synchronization process
       Log "Have not synced devices and usage already for this optimization run...enqueuing"

Step 3: Clear Staging Tables
       Call TruncateStagingTables method with:
       - Logger instance
       - Jasper database connection string
       - Service provider ID
       Execute DeleteStagingWithPolicy for both device and usage staging repositories

Step 4: Calculate Full Sync Date Range
       Set lastSyncDate = DateTime.UtcNow.AddMonths(-1).AddDays(-1)
       This ensures sync of last 30+ days of device data
       Log calculated lastSyncDate for audit purposes

Step 5: Enqueue Device Sync Job
       Call EnqueueGetDeviceListAsync with:
       - Device sync queue URL
       - Service provider ID
       - Page number = 1 (start from first page)
       - Calculated lastSyncDate
       - AWS credentials
       - Logger instance
       - Optimization session ID
       Return early to allow sync completion before optimization
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 269-285
```csharp
if (!message.MessageAttributes.ContainsKey("HasSynced") || !bool.TryParse(message.MessageAttributes["HasSynced"].StringValue, out var hasSynced))
{
    hasSynced = false;
}
if (!hasSynced)
{
    if (isAutoCarrierOptimization)
    {
        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 20, "", additionalData);
    }
    logger.LogInfo("INFO", "Have not synced devices and usage already for this optimization run...enqueuing");
    TruncateStagingTables(logger, context.GeneralProviderSettings.JasperDbConnectionString, serviceProviderId);
    // sync anything that has changed in the last month
    DateTime lastSyncDate = DateTime.UtcNow.AddMonths(-1).AddDays(-1);
    var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
    await EnqueueGetDeviceListAsync(_deviceSyncQueueUrl, serviceProviderId, 1, lastSyncDate, awsCredentials, logger, optimizationSessionId);
    return;
}
```

**Supporting Code - Staging Table Truncation**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 426-434
```csharp
private void TruncateStagingTables(IKeysysLogger logger, string connectionString, int serviceProviderId)
{
    var errorMessages = new List<string>();
    var deviceStagingRepo = new JasperDeviceStagingRepository();
    deviceStagingRepo.DeleteStagingWithPolicy(logger, connectionString, serviceProviderId, errorMessages);

    var usageStagingRepo = new JasperUsageStagingRepository();
    usageStagingRepo.DeleteStagingWithPolicy(logger, connectionString, serviceProviderId, errorMessages);
}
```

---

## 2. Incremental Sync: Continues from last sync point for existing sessions

### What
Executes incremental device synchronization that continues from the last successful sync point without clearing staging tables for existing optimization sessions

### Why
Optimizes performance and reduces API calls by only retrieving device changes since the last sync, minimizing data transfer and processing time while maintaining data consistency for ongoing optimization sessions

### How
Uses the lastSyncDate parameter from SQS message attributes to determine the starting point for incremental sync and calls Jasper API with modifiedSince parameter

### Algorithm
```
ALGORITHM: ExecuteIncrementalSync
INPUT: Last Sync Date, Service Provider ID, Page Number
OUTPUT: Incremental Device Data Synchronization

Step 1: Extract Last Sync Date
       Parse lastSyncDate from SQS message attributes
       If parsing fails, default to conservative sync date
       Log lastSyncDate for audit and debugging purposes

Step 2: Validate Incremental Sync Parameters
       Verify lastSyncDate is valid and not in future
       Ensure service provider ID is valid
       Check page number is positive integer

Step 3: Construct Incremental API Call
       Build Jasper API URL with modifiedSince parameter
       Format: ProductionApiUrl/devices?modifiedSince={lastSyncDate:s}Z&pageNumber={pageNumber}
       Include proper authentication headers

Step 4: Execute Incremental Data Retrieval
       Call GetJasperDevices with:
       - Context and SQS values
       - Jasper authentication
       - Last sync date
       - Current page number
       Process response and extract device changes

Step 5: Process Incremental Changes
       Add new/modified devices to staging tables
       Update existing device records with changes
       Continue pagination until all changes retrieved
       Maintain sync checkpoint for next incremental sync
```

### Code Location
**File**: `AltaworxJasperAWSGetDevicesQueue.cs` - Lines 233-245
```csharp
private async Task<bool> GetJasperDevices(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, JasperAuthentication jasperAuth, DateTime lastSyncDate, int pageNumber)
{
    bool isLastPage = false;
    var decodedPassword = context.Base64Service.Base64Decode(jasperAuth.Password);
    using (HttpClient client = new HttpClient(new LambdaLoggingHandler()))
    {
        client.BaseAddress = new Uri($"{jasperAuth.ProductionApiUrl.TrimEnd('/')}/{JasperDevicesGetPath.TrimStart('/')}?modifiedSince={lastSyncDate:s}Z&pageNumber={pageNumber}");
        LogInfo(context, "Endpoint", client.BaseAddress);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var encoded = context.Base64Service.Base64Encode(jasperAuth.Username + ":" + decodedPassword);
        client.DefaultRequestHeaders.Add("Authorization", "Basic " + encoded);
        HttpResponseMessage response = await client.GetAsync(client.BaseAddress);
```

**Supporting Code - Message Queue Processing**:
**File**: `AltaworxJasperAWSGetDevicesQueue.cs` - Lines 130-142
```csharp
LogInfo(context, "JasperAPIUsername", jasperAuth.Username);
LogInfo(context, "LastSyncDate", sqsValues.LastSyncDate);
LogInfo(context, "PageNumber", sqsValues.PageNumber);

bool isLastPage = false;
while (sqsValues.PageCounter <= MaxPagesToProcess && sqsValues.Errors.Count <= MAX_HTTP_RETRY_FAILURE_COUNT)
{
    if (!isLastPage)
    {
        isLastPage = await GetJasperDevicesWithPolicy(context, sqsValues, jasperAuth, sqsValues.LastSyncDate, sqsValues.PageNumber);
        sqsValues.PageCounter++;
        sqsValues.PageNumber++;
    }
```

---

## 3. Staging Management: Uses dedicated staging tables for transaction isolation

### What
Utilizes dedicated staging tables (JasperDeviceStaging, JasperUsageStaging) to isolate sync transactions from production data and ensure data consistency during synchronization processes

### Why
Provides transaction isolation to prevent data corruption during sync operations, enables rollback capabilities in case of failures, and allows validation of synchronized data before committing to production tables

### How
Implements staging repositories with policy-based operations, bulk copy operations to staging tables, and stored procedures to move validated data from staging to production tables

### Algorithm
```
ALGORITHM: ManageStagingTables
INPUT: Device Data, Service Provider ID, Sync Operation Type
OUTPUT: Isolated Transaction Processing

Step 1: Initialize Staging Table Operations
       Create JasperDeviceStagingRepository instance
       Create JasperUsageStagingRepository instance
       Prepare error collection for policy-based operations

Step 2: Clear Staging Tables (Full Sync Only)
       If sync type is full sync (PageNumber == 1)
       Then call ClearJasperDeviceStagingWithPolicy
       Execute deviceStagingRepo.DeleteStagingWithPolicy with:
       - Logger instance
       - Connection string
       - Service provider ID
       - Error collection

Step 3: Bulk Load Data to Staging Tables
       Create DataTable with device schema:
       - ID, ICCID, Status, RatePlan, CommunicationPlan
       - CreatedBy, CreatedDate, ServiceProviderId
       Populate DataTable with synchronized device data
       Execute SqlBulkCopy with retry policy to staging table

Step 4: Validate Staging Data
       Apply business rules validation on staged data
       Check for duplicate ICCIDs within staging
       Verify required fields are populated
       Validate rate plan and communication plan references

Step 5: Promote Staging Data to Production
       If validation successful and isLastPage = true
       Then call UpdateJasperDevices stored procedure
       Execute usp_Update_Jasper_Device with:
       - isLastPage flag
       - BillingCycleEndDay and BillingCycleEndHour
       - ServiceProviderId and IntegrationId
       Move validated data from staging to production tables
```

### Code Location
**File**: `AltaworxJasperAWSGetDevicesQueue.cs` - Lines 85-90 (Staging Clear)
```csharp
if (sqsValues.PageNumber == 1)
{
    ClearJasperDeviceStagingWithPolicy(keysysContext, sqsValues);
}
```

**File**: `AltaworxJasperAWSGetDevicesQueue.cs` - Lines 154-195 (Staging Management)
```csharp
DataTable table = new DataTable();
table.Columns.Add("ID");
table.Columns.Add("ICCID");
table.Columns.Add("Status");
table.Columns.Add("RatePlan");
table.Columns.Add("CommunicationPlan");
table.Columns.Add("CreatedBy");
table.Columns.Add("CreatedDate");
table.Columns.Add("ServiceProviderId");

if (sqsValues.JasperDeviceList.Count > 0)
{
    foreach (var jasperDevice in sqsValues.JasperDeviceList)
    {
        var dr = AddToDataRow(table, jasperDevice);
        table.Rows.Add(dr);
    }
    LogInfo(context, "STATUS", "SQL Bulk Copy Start");

    var sqlBulkCopyRetryPolicy = GetSqlTransientPolicy(context.logger, sqsValues,
        $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::SqlBulkCopy:JasperDeviceStaging");
    sqlBulkCopyRetryPolicy.Execute(() => SqlBulkCopy(context, context.GeneralProviderSettings.JasperDbConnectionString, table, "JasperDeviceStaging"));

    var sqlUpdateJasperDevicesRetryPolicy = GetSqlTransientPolicy(context.logger, sqsValues,
        $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::UpdateJasperDevices");
    LogInfo(context, "STATUS", "Jasper Devices update done through Stored Procedure");
    sqlUpdateJasperDevicesRetryPolicy.Execute(() => UpdateJasperDevices(context, isLastPage, sqsValues.ServiceProviderId, jasperAuth));
}
```

**File**: `AltaworxJasperAWSGetDevicesQueue.cs` - Lines 319-340 (Staging Clear Policy)
```csharp
private void ClearJasperDeviceStagingWithPolicy(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
{
    var deviceStagingRepo = new JasperDeviceStagingRepository();
    deviceStagingRepo.DeleteStagingWithPolicy(context.logger,
        context.GeneralProviderSettings.JasperDbConnectionString,
        sqsValues.ServiceProviderId,
        sqsValues.Errors);
}
```

**File**: `AltaworxJasperAWSGetDevicesQueue.cs` - Lines 301-318 (Production Update)
```csharp
private static void UpdateJasperDevices(KeySysLambdaContext context, bool isLastPage, int serviceProviderId, JasperAuthentication jasperAuth)
{
    var serviceProvider = ServiceProviderCommon.GetServiceProvider(context.CentralDbConnectionString, serviceProviderId);

    using (var centralConn = new SqlConnection(context.CentralDbConnectionString))
    {
        using (var conn = new SqlConnection(context.GeneralProviderSettings.JasperDbConnectionString))
        {
            using (var cmd = new SqlCommand("usp_Update_Jasper_Device", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.Add(new SqlParameter("@isLastPage", isLastPage));
                cmd.Parameters.AddWithValue("@BillingCycleEndDay", jasperAuth.BillingPeriodEndDay);
                cmd.Parameters.AddWithValue("@BillingCycleEndHour", jasperAuth.BillingPeriodEndHour == null ? 0 : jasperAuth.BillingPeriodEndHour);
                cmd.Parameters.AddWithValue("@CentralDbName", centralConn.Database);
                cmd.Parameters.AddWithValue("@ServiceProviderId", serviceProviderId);
                cmd.Parameters.AddWithValue("@IntegrationId", serviceProvider.IntegrationId);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }
    }
}
```

---

## Summary

These three sync strategy rules work together to ensure:

1. **Data Freshness**: Full sync provides complete data refresh for new sessions (30+ day range)
2. **Performance Optimization**: Incremental sync minimizes data transfer for ongoing sessions (from last sync point)
3. **Transaction Safety**: Staging management ensures data integrity through isolated operations (dedicated staging tables)

The sync strategies are implemented across multiple files with `QueueCarrierPlanOptimization.cs` handling sync orchestration and `AltaworxJasperAWSGetDevicesQueue.cs` managing the actual data synchronization operations. This architecture provides a robust, scalable, and fault-tolerant device synchronization framework that balances performance with data consistency.