# Processing Steps Analysis

## Data Validation

**What**: Checks and assigns required device fields including ICCID, status, rate plan, and communication plan.  
**Why**: Ensures data integrity before database operations by validating essential device attributes.  
**How**: Maps device properties to structured data rows with required field assignments.

### Algorithm
```
For device validation V with required fields R:
Let required_fields = {iccid, status, ratePlan, communicationPlan, serviceProviderId}

For each device d ∈ device_list:
    data_row ← new_row()
    data_row[1] ← d.iccid
    data_row[2] ← d.status  
    data_row[3] ← d.ratePlan
    data_row[4] ← d.communicationPlan
    data_row[5] ← "AWS Lambda - Get Devices Service"
    data_row[6] ← current_timestamp()
    data_row[7] ← d.serviceProviderId
    
    Return validated_row
```

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Implementation: Lines 291-300 (`AddToDataRow` function)
- Field Assignment: Lines 293-299 (dr[1] through dr[7] assignments)

## Staging Insert

**What**: Performs bulk insert operations to transfer validated device data into JasperDeviceStaging table.  
**Why**: Optimizes database performance by batching multiple records in single transaction.  
**How**: Executes SQL bulk copy operations with retry policies for transient failures.

### Algorithm
```
For staging insert S with device batch B:
Let staging_table = "JasperDeviceStaging"
Let validated_devices = validated_device_list

For each device d ∈ validated_devices:
    table_row ← AddToDataRow(table, d)
    staging_table.Rows.Add(table_row)

Execute SqlBulkCopy(connection_string, staging_table, target_table)
With retry_policy = GetSqlTransientPolicy()
```

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Bulk Copy Call: Line 181 (`SqlBulkCopy(context, context.GeneralProviderSettings.JasperDbConnectionString, table, "JasperDeviceStaging")`)
- Retry Policy: Lines 179-180 (`GetSqlTransientPolicy` with staging context)
- Row Addition: Lines 173-177 (foreach loop adding rows to table)

## Master Update

**What**: Executes usp_Update_Jasper_Device stored procedure to synchronize master device records.  
**Why**: Maintains data consistency between staging and production tables through controlled updates.  
**How**: Calls parameterized stored procedure with validation flags and metadata.

### Algorithm
```
For master update M with staging data S:
Let procedure = "usp_Update_Jasper_Device"
Let parameters = {
    @isLastPage,
    @BillingCycleEndDay, 
    @BillingCycleEndHour,
    @CentralDbName,
    @ServiceProviderId,
    @IntegrationId
}

Execute stored_procedure(procedure, parameters)
With transaction_scope ∧ error_handling
```

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Procedure Call: Line 312 (`new SqlCommand("usp_Update_Jasper_Device", conn)`)
- Parameter Setup: Lines 315-320 (cmd.Parameters.AddWithValue for each parameter)
- Execution: Line 322 (`cmd.ExecuteNonQuery()`)

## Integration

**What**: Updates billing cycle metadata and integration settings through stored procedure parameters.  
**Why**: Synchronizes billing configuration and system integration data across components.  
**How**: Passes billing period timestamps and integration identifiers to database procedures.

### Algorithm
```
For integration update I with metadata M:
Let billing_metadata = {
    BillingCycleEndDay: jasperAuth.BillingPeriodEndDay,
    BillingCycleEndHour: jasperAuth.BillingPeriodEndHour ∨ 0
}
Let integration_data = {
    IntegrationId: serviceProvider.IntegrationId,
    CentralDbName: centralConnection.Database
}

Update integration_settings(billing_metadata ∪ integration_data)
```

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Billing Cycle: Lines 316-317 (`@BillingCycleEndDay`, `@BillingCycleEndHour` parameters)
- Integration ID: Line 320 (`@IntegrationId`, `serviceProvider.IntegrationId`)
- Database Context: Lines 318-319 (`@CentralDbName`, `@ServiceProviderId` parameters)