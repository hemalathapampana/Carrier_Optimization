# Queue Optimization Workflow Analysis

## Overview
This document analyzes the carrier plan optimization workflow covering six main processing steps from queue reception to result persistence.

---

## 1. Receive Queue IDs

### Definition
**What**: Processes incoming SQS messages containing service provider and billing period identifiers.  
**Why**: Initiates optimization workflow by validating required parameters for carrier plan analysis.  
**How**: Extracts and validates message attributes including ServiceProviderId, BillPeriodId, and TenantId.

### Algorithm
```mathematical
For queue message reception R with SQS event S:
Let required_attributes = {ServiceProviderId, BillPeriodId, TenantId}

For each message m ∈ S.Records:
    If m.MessageAttributes.ContainsKey(attr) ∀ attr ∈ required_attributes:
        Extract message_data = Parse(m.MessageAttributes)
        Return valid_message_data
    Else:
        Log("EXCEPTION", "Missing required attributes")
        Return invalid_message
```

### Code Implementation
```csharp
// Main SQS handler
public async Task Handler(SQSEvent sqsEvent, ILambdaContext context)
{
    KeySysLambdaContext keysysContext = BaseFunctionHandler(context);
    
    if (sqsEvent?.Records?.Count > 0)
    {
        var sqsMessage = sqsEvent.Records[0];
        if (sqsMessage.MessageAttributes.ContainsKey("OptimizationSessionId") && sqsMessage.MessageAttributes.ContainsKey("HasSynced"))
        {
            isAutoCarrierOptimization = true;
        }
        await ProcessEvent(keysysContext, serviceProviderRepository, sqsEvent);
    }
    else
    {
        isAutoCarrierOptimization = true;
        await QueueJasperServiceProviders(keysysContext);
    }
}

// Message attribute validation
private async Task ProcessEventRecord(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, SQSEvent.SQSMessage message)
{
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
    int billingPeriodId = int.Parse(message.MessageAttributes["BillPeriodId"].StringValue);
}
```

---

## 2. Validate Queue Status

### Definition
**What**: Verifies that optimization sessions are not already running for the tenant.  
**Why**: Prevents concurrent optimizations that could cause data conflicts and resource contention.  
**How**: Queries database for active optimization sessions and checks completion status.

### Algorithm
```mathematical
For queue status validation V with tenant T:
Let active_sessions = Query(vwOptimizationSessionRunning, T.tenantId)

If active_sessions ≠ ∅:
    current_time = DateTime.UtcNow
    billing_end = billingPeriod.BillingPeriodEnd
    
    If current_time.Date = billing_end.Date ∧ last_status = Completed:
        Return allow_new_optimization
    Else:
        Return optimization_already_running
Else:
    Return ready_for_optimization
```

### Code Implementation
```csharp
// Optimization running check
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

    // Allow re-run if today is the last day and last optimization is completed
    if (optimizationIdRunning >= 0)
    {
        var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, billingPeriod.BillingTimeZone);
        
        if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date)
        {
            var statusQuery = @"SELECT TOP 1 OptimizationInstanceStatusId 
                        FROM OptimizationInstance 
                        WHERE OptimizationSessionId = @optimizationIdRunning 
                        ORDER BY CreatedDate DESC";
            
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = statusQuery;
                    cmd.Parameters.AddWithValue("@optimizationIdRunning", optimizationIdRunning);
                    conn.Open();
                    
                    var status = cmd.ExecuteScalar();
                    if (status != null && (int)status == (int)OptimizationStatus.Completed)
                    {
                        return false; // Allow a new run
                    }
                }
            }
        }
    }
    
    return optimizationIdRunning >= 0;
}
```

---

## 3. Load Instance Data

### Definition
**What**: Creates optimization instance and loads billing period, rate plans, and communication plans.  
**Why**: Establishes optimization context with all necessary data for carrier plan analysis.  
**How**: Starts optimization session and instance, then retrieves carrier configuration data.

### Algorithm
```mathematical
For instance data loading I with session S:
Let optimization_session = StartOptimizationSession(S.tenantId, S.billingPeriod)
Let instance = StartOptimizationInstance(S.serviceProviderId, optimization_session)

Load configuration_data = {
    comm_plans = GetCommPlans(S.serviceProviderId),
    rate_plans = GetRatePlans(S.serviceProviderId),
    billing_period = GetBillingPeriod(S.billingPeriodId)
}

Return instance_context = {instance, configuration_data}
```

### Code Implementation
```csharp
// Instance creation and data loading
private async Task RunOptimizationByPortalType(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, int billingPeriodId, int serviceProviderId, int tenantId, long optimizationSessionId, PortalTypes portalType, string additionalData)
{
    // Start instance
    var billingPeriod = GetBillingPeriod(context, billingPeriodId);
    var integrationAuthenticationId = serviceProviderRepository.GetIntegrationAuthenticationId(serviceProviderId);

    long instanceId = StartOptimizationInstance(context, tenantId, serviceProviderId, null, null,
                    integrationAuthenticationId, billingPeriod.BillingPeriodStart, billingPeriod.BillingPeriodEnd,
                            portalType, optimizationSessionId, billingPeriodId, false, null);
    var instance = GetInstance(context, instanceId);

    if (portalType == PortalTypes.M2M)
    {
        await RunOptimization(context, tenantId, serviceProviderId, billingPeriodId, optimizationSessionId, billingPeriod, instance, additionalData, integrationAuthenticationId);
    }
    else if (portalType == PortalTypes.Mobility)
    {
        await RunMobilityOptimization(context, optimizationMobilityDeviceRepository, tenantId, serviceProviderId, billingPeriodId, optimizationSessionId, billingPeriod, instance, additionalData, integrationAuthenticationId);
    }
}

// Configuration data loading
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
        // Continue with optimization processing...
    }
}
```

---

## 4. Process Queues

### Definition
**What**: Creates communication plan groups and generates optimization queues for rate plan combinations.  
**Why**: Organizes devices by communication plans and creates work queues for parallel optimization processing.  
**How**: Groups communication plans, creates optimization queues, and generates rate plan sequences.

### Algorithm
```mathematical
For queue processing Q with comm_plans C and rate_plans R:
For each group g ∈ C.GroupBy(x => x.RatePlanIds):
    comm_plan_group_id = CreateCommPlanGroup(instance.Id)
    group_rate_plans = RatePlansForGroup(R, g)
    
    If |group_rate_plans| > 15:
        SendCarrierPlanLimitAlertEmail()
    Else If |group_rate_plans| > 1:
        rate_pool_sequences = GenerateRatePoolSequences(group_rate_plans)
        For each sequence s ∈ rate_pool_sequences:
            queue_id = CreateQueue(instance.Id, comm_plan_group_id)
            AddRatePlansToQueue(queue_id, s)
```

### Code Implementation
```csharp
// Communication plan group processing
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
        // permute rate plans
        var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);

        foreach (var ratePoolSequence in ratePoolSequences)
        {
            // add queue for rate plan permutation
            var queueId = CreateQueue(context, instance.Id, commPlanGroupId, serviceProviderId, usesProration);

            // add rate plans to queue
            var dtQueueRatePlanTemp = AddRatePlansToQueue(queueId, ratePoolSequence, commGroupRatePlanTable);
            if (dtQueueRatePlanTemp != null && dtQueueRatePlanTemp.Rows.Count > 0)
            {
                foreach (DataRow dr in dtQueueRatePlanTemp.Rows)
                {
                    dtQueueRatePlan.Rows.Add(dr.ItemArray);
                }
            }
        }
    }
}

// Bulk queue creation
public List<long> BulkCreateQueue(KeySysLambdaContext context, long instanceId, long commPlanGroupId, int serviceProviderId, bool usesProration, int sequenceCount, bool isBillInAdvance = false)
{
    var dataTable = BuildOptimizationQueueTable();
    for (int i = 0; i < sequenceCount; i++)
    {
        var dataRow = AddOptimizationQueueRow(dataTable, instanceId, commPlanGroupId, serviceProviderId, usesProration, isBillInAdvance);
        dataTable.Rows.Add(dataRow);
    }
    
    var logMessage = SqlHelper.SqlBulkCopy(context.ConnectionString, dataTable, DatabaseTableNames.OPITMIZATION_QUEUE, SQLBulkCopyHelper.AutoMapColumns(dataTable));
    LogInfo(context, CommonConstants.INFO, $"{sequenceCount} Queues Created");
    
    var parameters = new List<SqlParameter>()
    {
        new SqlParameter(CommonSQLParameterNames.COMM_GROUP_ID, commPlanGroupId),
        new SqlParameter(CommonSQLParameterNames.RUN_STATUS_ID, OptimizationStatus.NotStarted),
    };
    var queueIds = SqlQueryHelper.ExecuteStoredProcedureWithListResult(ParameterizedLog(context), context.ConnectionString, SQLConstant.StoredProcedureName.GET_OPTIMIZATION_QUEUE_IDS_BY_COMM_GROUP_ID, ReadQueueId, parameters, SQLConstant.ShortTimeoutSeconds);
    return queueIds;
}
```

---

## 5. Execute Optimization

### Definition
**What**: Runs carrier plan optimization calculations and generates rate plan sequences for processing.  
**Why**: Determines optimal rate plan assignments for devices based on usage patterns and costs.  
**How**: Calculates rate pools, assigns devices, and sends optimization queues for parallel execution.

### Algorithm
```mathematical
For optimization execution E with rate_plans R and devices D:
Let calculated_plans = RatePoolCalculator.CalculateMaxAvgUsage(R)
Let rate_pools = CreateRatePools(calculated_plans, billing_period)
Let rate_pool_collection = CreateRatePoolCollection(rate_pools)

device_assignment = BaseDeviceAssignment(D, rate_pool_collection)
rate_pool_sequences = GenerateRatePoolSequences(rate_pool_collection.RatePools)

Send optimization_queues to parallel_processors
```

### Code Implementation
```csharp
// Optimization calculation and execution
var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(groupRatePlans, null);
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
var ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools);

// base device assignment
List<string> commPlanNames = commPlanGroup.Select(x => x.CommunicationPlanName).ToList();
List<vwOptimizationSimCard> optimizationSimCards = GetOptimizationSimCards(context, commPlanNames, serviceProviderId, null, null, billingPeriod.Id, tenantId);
var commGroupSimCardCount = BaseDeviceAssignment(context, instance.Id, commPlanGroupId, serviceProviderId, null, null, commPlanNames, ratePoolCollection, ratePools, optimizationSimCards, billingPeriod, usesProration);

// zero sim card => no need to run optimizer
// one sim card => swapping between rate plans would be the same as base device assignment
if (commGroupSimCardCount > 1)
{
    // add rate plans to comm plan group
    DataTable commGroupRatePlanTable = AddCarrierRatePlansToCommPlanGroup(context, instance.Id, commPlanGroupId, calculatedPlans);

    // permute rate plans
    var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);

    // Send optimization queues for parallel processing
    await SendMessageToCreateQueueRatePlans(context, ratePoolSequences, commPlanGroupId);
}

// Send rate plan sequences for processing
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

### Definition
**What**: Persists optimization queue configurations and rate plan sequences to database.  
**Why**: Stores optimization setup for execution by worker processes and result tracking.  
**How**: Bulk inserts queue data and rate plan sequences using SQL bulk copy operations.

### Algorithm
```mathematical
For result saving S with queues Q and sequences R:
Let queue_rate_plan_table = DataTable()

For each sequence s ∈ R:
    queue_data = AddRatePlansToQueue(s.QueueId, s, rate_plan_table)
    queue_rate_plan_table.AddRows(queue_data)

BulkInsert(queue_rate_plan_table, "OptimizationQueue_RatePlan")
SendRunOptimizerMessage(R, batch_size)
```

### Code Implementation
```csharp
// Rate plan sequence processing and saving
private async Task ProcessRatePlanSequences(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    var sequences = JsonSerializer.Deserialize<RatePlanSequence[]>(message.MessageAttributes[SQSMessageKeyConstant.RATE_PLAN_SEQUENCES].StringValue);
    var isCommGroupIdParsed = int.TryParse(message.MessageAttributes[SQSMessageKeyConstant.COMM_GROUP_ID].StringValue, out var commGroupId);
    
    if (isCommGroupIdParsed && sequences != null && sequences.Length > 0)
    {
        //Add the rate plans sequences to database
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
            // add rate plans to queue
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

// Bulk data operations
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

---

## Environment Configuration
```csharp
private string _carrierOptimizationQueueUrl = Environment.GetEnvironmentVariable("CarrierOptimizationQueueURL");
private string _deviceSyncQueueUrl = Environment.GetEnvironmentVariable("DeviceSyncQueueURL");
private readonly int DEFAULT_QUEUES_PER_INSTANCE = 5;
private int QueuesPerInstance = Convert.ToInt32(Environment.GetEnvironmentVariable("QueuesPerInstance"));
private string ErrorNotificationEmailReceiver = Environment.GetEnvironmentVariable("ErrorNotificationEmailReceiver");
```