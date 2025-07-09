# Queue Optimization Workflow - Focused Analysis

## 1. Receive Queue IDs

**What**: Processes incoming SQS messages containing service provider and billing period identifiers.  
**Why**: Initiates optimization workflow by validating required parameters for carrier plan analysis.  
**How**: Extracts and validates message attributes including ServiceProviderId, BillPeriodId, and TenantId.

### Algorithm
```
STEP 1: Receive SQS Event
    IF sqsEvent has Records
        Get first message from Records
        
STEP 2: Check Message Attributes
    IF message contains "OptimizationSessionId" AND "HasSynced"
        Set isAutoCarrierOptimization = true
        
STEP 3: Validate Required Fields
    CHECK if message has "ServiceProviderId"
        IF missing: Log error and exit
    CHECK if message has "BillPeriodId" 
        IF missing: Log error and exit
    CHECK if message has "TenantId"
        IF missing: Log error and exit
        
STEP 4: Extract Message Data
    Parse ServiceProviderId from message
    Parse BillPeriodId from message  
    Parse TenantId from message
    Continue to next step
```

### Code Location
```csharp
// Main SQS handler - QueueCarrierPlanOptimization.cs
public async Task Handler(SQSEvent sqsEvent, ILambdaContext context)
{
    if (sqsEvent?.Records?.Count > 0)
    {
        var sqsMessage = sqsEvent.Records[0];
        if (sqsMessage.MessageAttributes.ContainsKey("OptimizationSessionId") && sqsMessage.MessageAttributes.ContainsKey("HasSynced"))
        {
            isAutoCarrierOptimization = true;
        }
        await ProcessEvent(keysysContext, serviceProviderRepository, sqsEvent);
    }
}

// Message validation - ProcessEventRecord method
if (!message.MessageAttributes.ContainsKey("ServiceProviderId"))
{
    logger.LogInfo("EXCEPTION", "No Service Provider Id provided in message");
    return;
}

if (!message.MessageAttributes.ContainsKey("BillPeriodId"))
{
    logger.LogInfo("EXCEPTION", "No Billing Period provided in message");
    return;
}

int serviceProviderId = int.Parse(message.MessageAttributes["ServiceProviderId"].StringValue);
int tenantId = int.Parse(message.MessageAttributes["TenantId"].StringValue);
```

---

## 2. Validate Queue Status

**What**: Verifies that optimization sessions are not already running for the tenant.  
**Why**: Prevents concurrent optimizations that could cause data conflicts and resource contention.  
**How**: Queries database for active optimization sessions and checks completion status.

### Algorithm
```
STEP 1: Query Active Optimizations
    Search database for running optimization sessions
    Use vwOptimizationSessionRunning view
    Filter by TenantId from message
    
STEP 2: Check If Optimization Running
    IF no active sessions found
        Return "ready for optimization"
        Continue to next step
        
STEP 3: Handle Active Sessions
    IF optimization is currently running
        Get current time and billing period end date
        
        IF today is last day of billing period AND last optimization completed
            Allow new optimization run
        ELSE
            Log warning message
            Send alert email to administrators
            Stop processing and exit
            
STEP 4: Start New Session
    IF optimization allowed
        Create new optimization session
        Generate session ID for tracking
```

### Code Location
```csharp
// Optimization status check - IsOptimizationRunning method
private bool IsOptimizationRunning(KeySysLambdaContext context, int tenantId)
{
    var optimizationIdRunning = -1;
    var queryText = @"SELECT OptimizationSessionId FROM vwOptimizationSessionRunning sr
                        JOIN (SELECT TOP 1 * FROM vwOptimizationSession
                            WHERE TenantId = @tenantId
                            AND IsActive = 1
                            AND IsDeleted = 0
                            ORDER BY CreatedDate DESC) optf ON sr.OptimizationSessionId = optf.id
                        WHERE SR.OptimizationQueueStatusId != @optimizationStatusError OR OptimizationInstanceStatusId != @optimizationStatusError";
    
    using (var conn = new SqlConnection(context.ConnectionString))
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = queryText;
            cmd.Parameters.AddWithValue("@optimizationStatusError", (int)OptimizationStatus.CompleteWithErrors);
            cmd.Parameters.AddWithValue("@tenantId", tenantId);
            conn.Open();
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                int.TryParse(result.ToString(), out optimizationIdRunning);
            }
        }
    }
    return optimizationIdRunning >= 0;
}

// Validation logic in ProcessEventRecord
var isOptRunning = IsOptimizationRunning(context, tenantId);
if (!isOptRunning)
{
    var billingPeriod = GetBillingPeriod(context, billingPeriodId.Value);
    optimizationSessionId = await StartOptimizationSession(context, tenantId, billingPeriod);
}
else
{
    logger.LogInfo("WARN", "A Carrier Optimization has not been completed so another optimization will not be triggered.");
    var subject = "[Warning] Carrier Plan Optimization: Optimization Is Already Running";
    var body = BuildOptRunningAlertEmailBody(context);
    SendAlertEmail(context, subject, body);
    return;
}
```

---

## 3. Load Instance Data

**What**: Creates optimization instance and loads billing period, rate plans, and communication plans.  
**Why**: Establishes optimization context with all necessary data for carrier plan analysis.  
**How**: Starts optimization session and instance, then retrieves carrier configuration data.

### Algorithm
```
STEP 1: Get Billing Period Data
    Retrieve billing period using BillPeriodId
    Get billing start and end dates
    Get billing timezone information
    
STEP 2: Create Optimization Instance
    Get integration authentication ID for service provider
    Create new optimization instance with:
        - TenantId, ServiceProviderId
        - Billing period start/end dates
        - Portal type (M2M or Mobility)
        - Optimization session ID
        
STEP 3: Load Configuration Data
    Get communication plans for service provider
    Get rate plans for service provider
    Get device settings (proration options)
    Get integration type (Jasper, Rogers, etc.)
    
STEP 4: Validate Data
    CHECK if communication plans exist
    CHECK if rate plans exist
    IF either missing: Log error and stop
    
STEP 5: Calculate Expected Device Count
    Query expected SIM card count for optimization
    Use service provider and billing period
    Store count for validation later
```

### Code Location
```csharp
// Instance creation - RunOptimizationByPortalType method
private async Task RunOptimizationByPortalType(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, int billingPeriodId, int serviceProviderId, int tenantId, long optimizationSessionId, PortalTypes portalType, string additionalData)
{
    var billingPeriod = GetBillingPeriod(context, billingPeriodId);
    var integrationAuthenticationId = serviceProviderRepository.GetIntegrationAuthenticationId(serviceProviderId);

    long instanceId = StartOptimizationInstance(context, tenantId, serviceProviderId, null, null,
                    integrationAuthenticationId, billingPeriod.BillingPeriodStart, billingPeriod.BillingPeriodEnd,
                            portalType, optimizationSessionId, billingPeriodId, false, null);
    var instance = GetInstance(context, instanceId);
}

// Data loading - RunOptimization method
private async Task RunOptimization(KeySysLambdaContext context, int tenantId, int serviceProviderId, int billingPeriodId, long optimizationSessionId, BillingPeriod billingPeriod, OptimizationInstance instance, string additionalData, int? integrationAuthenticationId)
{
    var instanceType = (IntegrationType)instance.IntegrationId;
    var jasperProviderSettings = context.SettingsRepo.GetJasperDeviceSettings(serviceProviderId);
    var usesProration = jasperProviderSettings.UsesProration;

    // get carrier rate plans and comm plans
    var commPlans = GetCommPlans(context, serviceProviderId);
    var ratePlans = GetRatePlans(context, serviceProviderId);
    
    if (commPlans != null && commPlans.Count > 0 && ratePlans != null && ratePlans.Count > 0)
    {
        int expectedSimCount = GetExpectedOptimizationSimCardCount(context, serviceProviderId, null, billingPeriodId, integrationAuthenticationId, tenantId);
    }
}
```

---

## 4. Process Queues

**What**: Creates communication plan groups and generates optimization queues for rate plan combinations.  
**Why**: Organizes devices by communication plans and creates work queues for parallel optimization processing.  
**How**: Groups communication plans, creates optimization queues, and generates rate plan sequences.

### Algorithm
```
STEP 1: Group Communication Plans
    Take all communication plans for service provider
    Group them by RatePlanIds (plans that share same rate plans)
    Filter out plans with empty RatePlanIds
    
STEP 2: Process Each Communication Plan Group
    FOR each group of communication plans:
        Create new communication plan group in database
        Add all communication plans to this group
        
STEP 3: Get Rate Plans for Group
    Find all rate plans that match this group
    Filter rate plans based on group's RatePlanIds
    
STEP 4: Validate Rate Plan Count
    COUNT rate plans in group
    IF count > 15:
        Send alert email to administrators
        Log error about rate plan limit exceeded
        Skip this group
        
STEP 5: Create Optimization Queues
    IF rate plan count between 2 and 15:
        Generate all possible rate plan sequences
        FOR each rate plan sequence:
            Create new optimization queue
            Add rate plans to queue in sequence order
            Store queue for parallel processing
            
STEP 6: Bulk Create All Queues
    Create DataTable with queue information
    Use SQL bulk copy for performance
    Get generated queue IDs back from database
```

### Code Location
```csharp
// Queue processing - RunOptimization method
foreach (var commPlanGroup in commPlans.Where(x => !string.IsNullOrWhiteSpace(x.RatePlanIds)).GroupBy(x => x.RatePlanIds))
{
    // create new comm plan group
    long commPlanGroupId = CreateCommPlanGroup(context, instance.Id);
    commPlanGroupIds.Add(commPlanGroupId);

    // add comm plans to comm plan group
    AddCommPlansToCommPlanGroup(context, instance.Id, commPlanGroupId, commPlanGroup);

    // get rate plans for group
    var groupRatePlans = RatePlansForGroup(ratePlans, commPlanGroup);

    //Rate plans are limited to 15. If the count is greater than 15 send an error email
    if (groupRatePlans.Count > 15)
    {
        LogInfo(context, "ERROR", $"The rate plan count exceeds the limit of 15 for Instance: {instance.Id}");
        SendCarrierPlanLimitAlertEmail(context, instance);
    }
    else if (groupRatePlans.Count > 1)
    {
        var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);

        foreach (var ratePoolSequence in ratePoolSequences)
        {
            var queueId = CreateQueue(context, instance.Id, commPlanGroupId, serviceProviderId, usesProration);
            var dtQueueRatePlanTemp = AddRatePlansToQueue(queueId, ratePoolSequence, commGroupRatePlanTable);
        }
    }
}

// Bulk queue creation - BulkCreateQueue method
public List<long> BulkCreateQueue(KeySysLambdaContext context, long instanceId, long commPlanGroupId, int serviceProviderId, bool usesProration, int sequenceCount, bool isBillInAdvance = false)
{
    var dataTable = BuildOptimizationQueueTable();
    for (int i = 0; i < sequenceCount; i++)
    {
        var dataRow = AddOptimizationQueueRow(dataTable, instanceId, commPlanGroupId, serviceProviderId, usesProration, isBillInAdvance);
        dataTable.Rows.Add(dataRow);
    }
    var logMessage = SqlHelper.SqlBulkCopy(context.ConnectionString, dataTable, DatabaseTableNames.OPITMIZATION_QUEUE, SQLBulkCopyHelper.AutoMapColumns(dataTable));
    return queueIds;
}
```

---

## 5. Execute Optimization

**What**: Runs carrier plan optimization calculations and generates rate plan sequences for processing.  
**Why**: Determines optimal rate plan assignments for devices based on usage patterns and costs.  
**How**: Calculates rate pools, assigns devices, and sends optimization queues for parallel execution.

### Algorithm
```
STEP 1: Calculate Rate Plan Usage
    FOR each rate plan in group:
        Calculate maximum average usage
        Determine data allowances and overage rates
        Validate overage charges are greater than 0
        
STEP 2: Create Rate Pools
    Convert calculated plans to rate pools
    Apply billing period settings
    Include proration if enabled
    Set charge type to RateChargeAndOverage
    
STEP 3: Create Rate Pool Collection
    Combine all rate pools into collection
    Organize for optimization processing
    
STEP 4: Assign Devices to Base Plans
    Get all SIM cards for communication plan names
    Assign each device to best initial rate plan
    Calculate initial cost baseline
    COUNT devices assigned (must be > 1 for optimization)
    
STEP 5: Generate Rate Plan Sequences
    IF device count > 1:
        Create all possible rate plan combinations
        Generate sequences for parallel testing
        Each sequence represents different plan assignment
        
STEP 6: Send to Parallel Processing
    Break sequences into batches
    Create SQS messages with rate plan sequences
    Send to optimization queue for worker processing
    Include communication group ID for tracking
```

### Code Location
```csharp
// Optimization execution - RunOptimization method
var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(groupRatePlans, null);
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
var ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools);

// base device assignment
List<string> commPlanNames = commPlanGroup.Select(x => x.CommunicationPlanName).ToList();
List<vwOptimizationSimCard> optimizationSimCards = GetOptimizationSimCards(context, commPlanNames, serviceProviderId, null, null, billingPeriod.Id, tenantId);
var commGroupSimCardCount = BaseDeviceAssignment(context, instance.Id, commPlanGroupId, serviceProviderId, null, null, commPlanNames, ratePoolCollection, ratePools, optimizationSimCards, billingPeriod, usesProration);

if (commGroupSimCardCount > 1)
{
    DataTable commGroupRatePlanTable = AddCarrierRatePlansToCommPlanGroup(context, instance.Id, commPlanGroupId, calculatedPlans);
    var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);
    await SendMessageToCreateQueueRatePlans(context, ratePoolSequences, commPlanGroupId);
}

// Send sequences for processing - SendMessageToCreateQueueRatePlans method
private async Task SendMessageToCreateQueueRatePlans(KeySysLambdaContext context, List<RatePlanSequence> ratePoolSequences, long commGroupId)
{
    var ratePoolBatches = ratePoolSequences.Chunk(OptimizationConstant.RATE_PLAN_SEQUENCES_BATCH_SIZE);
    foreach (var sequences in ratePoolBatches)
    {
        var attributes = new Dictionary<string, string>()
        {
            {SQSMessageKeyConstant.RATE_PLAN_SEQUENCES, JsonSerializer.Serialize(sequences)},
            {SQSMessageKeyConstant.COMM_GROUP_ID, commGroupId.ToString()},
        };
        await sqsService.SendSQSMessage(ParameterizedLog(context), AwsCredentials(context.Base64Service, context.GeneralProviderSettings.AWSAccesKeyID, context.GeneralProviderSettings.AWSSecretAccessKey), _carrierOptimizationQueueUrl, attributes);
    }
}
```

---

## 6. Save Results

**What**: Persists optimization queue configurations and rate plan sequences to database.  
**Why**: Stores optimization setup for execution by worker processes and result tracking.  
**How**: Bulk inserts queue data and rate plan sequences using SQL bulk copy operations.

### Algorithm
```
STEP 1: Receive Rate Plan Sequences
    Deserialize rate plan sequences from SQS message
    Extract communication group ID from message
    Validate both sequences and group ID exist
    
STEP 2: Prepare Data Tables
    Create DataTable for queue-rate plan mappings
    Add columns: QueueId, CommGroupRatePlanId, SequenceOrder, CreatedBy, CreatedDate
    
STEP 3: Load Existing Rate Plan Data
    Query database for communication group rate plans
    Get mapping of rate plan IDs to database IDs
    Load into DataTable for reference
    
STEP 4: Process Each Sequence
    FOR each rate plan sequence:
        Get rate plans for this queue
        Add queue-rate plan mappings to data table
        Set sequence order for each rate plan
        Add creation metadata (created by, date)
        
STEP 5: Bulk Save to Database
    Use SQL bulk copy to insert queue-rate plan data
    Insert into OptimizationQueue_RatePlan table
    Batch insert for performance
    
STEP 6: Trigger Optimizer Workers
    Send message to run optimizer workers
    Include sequence data and batch size
    Workers will process queues in parallel
    Each worker handles subset of optimization queues
```

### Code Location
```csharp
// Result saving - ProcessRatePlanSequences method
private async Task ProcessRatePlanSequences(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    var sequences = JsonSerializer.Deserialize<RatePlanSequence[]>(message.MessageAttributes[SQSMessageKeyConstant.RATE_PLAN_SEQUENCES].StringValue);
    var isCommGroupIdParsed = int.TryParse(message.MessageAttributes[SQSMessageKeyConstant.COMM_GROUP_ID].StringValue, out var commGroupId);
    
    if (isCommGroupIdParsed && sequences != null && sequences.Length > 0)
    {
        DataTable dtQueueRatePlan = new DataTable();
        dtQueueRatePlan.Columns.Add(CommonColumnNames.QueueId, typeof(long));
        dtQueueRatePlan.Columns.Add(CommonColumnNames.CommGroupRatePlanId, typeof(long));
        dtQueueRatePlan.Columns.Add(CommonColumnNames.SequenceOrder, typeof(int));
        dtQueueRatePlan.Columns.Add(CommonColumnNames.CreatedBy);
        dtQueueRatePlan.Columns.Add(CommonColumnNames.CreatedDate, typeof(DateTime));

        DataTable commGroupRatePlanTable = new DataTable();
        using (var connection = new SqlConnection(context.ConnectionString))
        {
            using (var cmd = new SqlCommand("SELECT Id, InstanceId, CommGroupId, CarrierRatePlanId, CustomerRatePlanId, MaxAvgUsage, CreatedBy, CreatedDate FROM OptimizationCommGroup_RatePlan WHERE CommGroupId = @CommGroupId", connection))
            {
                cmd.Parameters.AddWithValue("@CommGroupId", commGroupId);
                connection.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    commGroupRatePlanTable.Load(reader);
                }
            }
        }
        
        foreach (var sequence in sequences)
        {
            var dtQueueRatePlanTemp = AddRatePlansToQueue(sequence.QueueId, sequence, commGroupRatePlanTable);
            if (dtQueueRatePlanTemp != null && dtQueueRatePlanTemp.Rows.Count > 0)
            {
                foreach (DataRow dr in dtQueueRatePlanTemp.Rows)
                {
                    dtQueueRatePlan.Rows.Add(dr.ItemArray);
                }
            }
        }

        CreateQueueRatePlans(context, dtQueueRatePlan);
        await SendRunOptimizerMessage(context, sequences, QueuesPerInstance);
    }
}

// Bulk save operations - BulkSaveRatePlanAndSequences method
private void BulkSaveRatePlanAndSequences(KeySysLambdaContext context, int serviceProviderId, OptimizationInstance instance, bool usesProration, long sameRatePlansCollectionId, List<RatePlanSequence> ratePoolSequences)
{
    var queueIds = BulkCreateQueue(context, instance.Id, sameRatePlansCollectionId, serviceProviderId, usesProration, ratePoolSequences.Count, instance.UseBillInAdvance);
    if (queueIds == null || queueIds.Count < ratePoolSequences.Count)
    {
        throw new InvalidOperationException($"Only {queueIds?.Count} queue Ids created. The number of queue Ids should match the number of sequences {ratePoolSequences.Count}");
    }
    
    for (int i = 0; i < ratePoolSequences.Count; i++)
    {
        var ratePoolSequence = ratePoolSequences[i];
        var queueId = queueIds[i];
        ratePoolSequence.QueueId = queueId;
        ratePoolSequences[i] = ratePoolSequence;
    }
}
```