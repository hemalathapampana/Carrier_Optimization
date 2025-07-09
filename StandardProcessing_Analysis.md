# Standard Processing Analysis

## 1. Loads All Required Data for Optimization

**What**: Retrieves and validates all necessary data including devices, rate plans, communication plans, and billing information.  
**Why**: Ensures optimization algorithm has complete dataset to make accurate cost calculations and plan recommendations.  
**How**: Queries multiple data sources and validates data integrity before proceeding with optimization calculations.

### Algorithm
```
STEP 1: Load Communication Plans
    Get all communication plans for service provider
    Filter plans that have rate plan assignments
    Group plans by shared rate plan IDs
    
STEP 2: Load Rate Plans  
    Get all available rate plans for service provider
    Filter rate plans based on communication plan groups
    Validate rate plan data (overage rates, data allowances)
    
STEP 3: Load Device Data
    Get all SIM cards/devices for communication plans
    Include device usage history for billing period
    Filter active devices eligible for optimization
    
STEP 4: Load Billing Period Information
    Get billing period start and end dates
    Get timezone information for calculations
    Determine if proration should be applied
    
STEP 5: Validate Data Completeness
    CHECK communication plans exist and have rate plans
    CHECK rate plans have valid pricing data
    CHECK devices exist for optimization
    IF any validation fails: Log error and stop processing
    
STEP 6: Calculate Expected Counts
    Count expected SIM cards for optimization
    Validate actual count matches expected
    IF counts don't match: Send alert email
```

### Code Location
```csharp
// Data loading - RunOptimization method in QueueCarrierPlanOptimization.cs
private async Task RunOptimization(KeySysLambdaContext context, int tenantId, int serviceProviderId, int billingPeriodId, long optimizationSessionId, BillingPeriod billingPeriod, OptimizationInstance instance, string additionalData, int? integrationAuthenticationId)
{
    var instanceType = (IntegrationType)instance.IntegrationId;
    var usesProration = false;
    
    // Load proration settings
    if (instanceType == IntegrationType.Jasper || instanceType == IntegrationType.POD19 || instanceType == IntegrationType.TMobileJasper || instanceType == IntegrationType.Rogers)
    {
        var jasperProviderSettings = context.SettingsRepo.GetJasperDeviceSettings(serviceProviderId);
        usesProration = jasperProviderSettings.UsesProration;
    }

    // get carrier rate plans and comm plans
    var commPlans = GetCommPlans(context, serviceProviderId);
    var ratePlans = GetRatePlans(context, serviceProviderId);
    
    if (commPlans != null && commPlans.Count > 0 && ratePlans != null && ratePlans.Count > 0)
    {
        // get expected SIM Card Count
        int expectedSimCount = GetExpectedOptimizationSimCardCount(context, serviceProviderId, null, billingPeriodId, integrationAuthenticationId, tenantId);

        List<long> commPlanGroupIds = new List<long>();
        int actualSimCount = 0;

        // Process each communication plan group
        foreach (var commPlanGroup in commPlans.Where(x => !string.IsNullOrWhiteSpace(x.RatePlanIds)).GroupBy(x => x.RatePlanIds))
        {
            // Create communication plan group
            long commPlanGroupId = CreateCommPlanGroup(context, instance.Id);
            commPlanGroupIds.Add(commPlanGroupId);

            // Add communication plans to group
            AddCommPlansToCommPlanGroup(context, instance.Id, commPlanGroupId, commPlanGroup);

            // Get rate plans for this group
            var groupRatePlans = RatePlansForGroup(ratePlans, commPlanGroup);
        }
    }
}

// Device data loading
List<string> commPlanNames = commPlanGroup.Select(x => x.CommunicationPlanName).ToList();
List<vwOptimizationSimCard> optimizationSimCards = GetOptimizationSimCards(context, commPlanNames, serviceProviderId, null, null, billingPeriod.Id, tenantId);

// Validation and alert
if (actualSimCount < expectedSimCount)
{
    SendCarrierSimCardCountAlertEmail(context, instance, expectedSimCount, actualSimCount);
}
```

---

## 2. Executes Full Optimization Algorithm

**What**: Runs comprehensive rate plan optimization calculations to find optimal device-to-plan assignments.  
**Why**: Determines the most cost-effective rate plan assignments for all devices based on usage patterns and costs.  
**How**: Calculates rate pools, performs base device assignment, generates plan sequences, and executes parallel optimization processing.

### Algorithm
```
STEP 1: Calculate Rate Pool Usage
    FOR each rate plan in group:
        Calculate maximum average usage allowance
        Determine overage rates and charges
        Validate all rate plan pricing data
        Create calculated rate plan objects
        
STEP 2: Create Rate Pools
    Convert calculated plans into rate pools
    Apply billing period constraints
    Include proration calculations if enabled
    Set optimization charge type (rate + overage)
    
STEP 3: Build Rate Pool Collection
    Combine all rate pools into organized collection
    Prepare for optimization algorithm processing
    
STEP 4: Perform Base Device Assignment
    Get all eligible SIM cards for communication plans
    Assign each device to initial best-fit rate plan
    Calculate baseline costs for comparison
    Count total devices assigned
    
STEP 5: Validate Device Count for Optimization
    IF device count <= 1:
        Skip optimization (not enough devices to optimize)
        Use base assignment as final result
    ELSE:
        Continue with full optimization
        
STEP 6: Generate Rate Plan Sequences
    Create all possible rate plan assignment combinations
    Generate sequences for parallel testing
    Each sequence represents different optimization strategy
    
STEP 7: Create Optimization Queues
    FOR each rate plan sequence:
        Create optimization queue in database
        Assign rate plans to queue in sequence order
        Add queue metadata (instance, group, proration settings)
        
STEP 8: Execute Parallel Optimization
    Send rate plan sequences to worker processors
    Break sequences into manageable batches
    Distribute work across multiple optimization workers
    Each worker tests different plan assignment scenarios
```

### Code Location
```csharp
// Full optimization algorithm - RunOptimization method continued
if (groupRatePlans.Count > 15)
{
    LogInfo(context, "ERROR", $"The rate plan count exceeds the limit of 15 for Instance: {instance.Id}");
    SendCarrierPlanLimitAlertEmail(context, instance);
}
else if (groupRatePlans.Count == 0)
{
    LogInfo(context, "WARNING", $"The rate plan count is zero for this comm plan group");
}
else
{
    // Validate rate plan data
    if (groupRatePlans.Any(groupRatePlan => groupRatePlan.DataPerOverageCharge <= 0 || groupRatePlan.OverageRate <= 0))
    {
        LogInfo(context, "ERROR", "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate");
        StopOptimizationInstance(context, instance.Id, OptimizationStatus.CompleteWithErrors);
        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 0, "", additionalData);
        return;
    }

    // Execute optimization calculations
    var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(groupRatePlans, null);
    var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
    var ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools);

    // Base device assignment
    List<string> commPlanNames = commPlanGroup.Select(x => x.CommunicationPlanName).ToList();
    List<vwOptimizationSimCard> optimizationSimCards = GetOptimizationSimCards(context, commPlanNames, serviceProviderId, null, null, billingPeriod.Id, tenantId);
    var commGroupSimCardCount = BaseDeviceAssignment(context, instance.Id, commPlanGroupId, serviceProviderId, null, null, commPlanNames, ratePoolCollection, ratePools, optimizationSimCards, billingPeriod, usesProration);

    actualSimCount += commGroupSimCardCount;

    // zero sim card => no need to run optimizer
    // one sim card => swapping between rate plans would be the same as base device assignment
    if (commGroupSimCardCount > 1)
    {
        // add rate plans to comm plan group
        DataTable commGroupRatePlanTable = AddCarrierRatePlansToCommPlanGroup(context, instance.Id, commPlanGroupId, calculatedPlans);

        // permute rate plans
        var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);

        // Create optimization queues
        DataTable dtQueueRatePlan = new DataTable();
        dtQueueRatePlan.Columns.Add("QueueId", typeof(long));
        dtQueueRatePlan.Columns.Add("CommGroup_RatePlanId", typeof(long));
        dtQueueRatePlan.Columns.Add("SequenceOrder", typeof(int));
        dtQueueRatePlan.Columns.Add("CreatedBy");
        dtQueueRatePlan.Columns.Add("CreatedDate", typeof(DateTime));

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
```

---

## 3. Saves Results to Database

**What**: Persists optimization configuration, queue assignments, and rate plan sequences to database for worker processing.  
**Why**: Stores optimization setup and results for execution tracking, auditing, and parallel worker consumption.  
**How**: Uses bulk database operations to efficiently save queue data, rate plan mappings, and optimization metadata.

### Algorithm
```
STEP 1: Prepare Queue Data Tables
    Create DataTable for optimization queues
    Add columns: InstanceId, CommPlanGroupId, RunStatusId, ServiceProviderId, UsesProration
    Populate with queue information for bulk insert
    
STEP 2: Bulk Create Optimization Queues
    Use SQL bulk copy to insert queue records
    Insert into OptimizationQueue table
    Get generated queue IDs back from database
    
STEP 3: Prepare Rate Plan Mapping Data
    Create DataTable for queue-rate plan relationships
    Add columns: QueueId, CommGroup_RatePlanId, SequenceOrder, CreatedBy, CreatedDate
    
STEP 4: Process Rate Plan Sequences
    FOR each optimization queue:
        Map rate plans to queue in sequence order
        Add queue-rate plan relationships to data table
        Include sequence order for optimization processing
        Add creation metadata
        
STEP 5: Bulk Save Rate Plan Mappings
    Use SQL bulk copy to insert queue-rate plan data
    Insert into OptimizationQueue_RatePlan table
    Batch operations for performance
    
STEP 6: Update Optimization Status
    Set optimization instance status to processing
    Update progress indicators for monitoring
    Log completion of setup phase
    
STEP 7: Trigger Worker Processing
    Send SQS messages to optimization workers
    Include rate plan sequences and metadata
    Break work into batches for parallel processing
    Workers will execute actual optimization calculations
```

### Code Location
```csharp
// Database saving operations - Multiple methods in QueueCarrierPlanOptimization.cs

// Bulk queue creation
public List<long> BulkCreateQueue(KeySysLambdaContext context, long instanceId, long commPlanGroupId, int serviceProviderId, bool usesProration, int sequenceCount, bool isBillInAdvance = false)
{
    LogInfo(context, CommonConstants.SUB, $"(,{instanceId},{commPlanGroupId},{serviceProviderId},{usesProration})");
    var dataTable = BuildOptimizationQueueTable();
    for (int i = 0; i < sequenceCount; i++)
    {
        var dataRow = AddOptimizationQueueRow(dataTable, instanceId, commPlanGroupId, serviceProviderId, usesProration, isBillInAdvance);
        dataTable.Rows.Add(dataRow);
    }
    var logMessage = SqlHelper.SqlBulkCopy(context.ConnectionString, dataTable, DatabaseTableNames.OPITMIZATION_QUEUE, SQLBulkCopyHelper.AutoMapColumns(dataTable));
    LogInfo(context, CommonConstants.INFO, logMessage);
    LogInfo(context, CommonConstants.INFO, $"{sequenceCount} Queues Created");
    
    // Get queue IDs
    var parameters = new List<SqlParameter>()
    {
        new SqlParameter(CommonSQLParameterNames.COMM_GROUP_ID, commPlanGroupId),
        new SqlParameter(CommonSQLParameterNames.RUN_STATUS_ID, OptimizationStatus.NotStarted),
    };
    var queueIds = SqlQueryHelper.ExecuteStoredProcedureWithListResult(ParameterizedLog(context), context.ConnectionString, SQLConstant.StoredProcedureName.GET_OPTIMIZATION_QUEUE_IDS_BY_COMM_GROUP_ID, ReadQueueId, parameters, SQLConstant.ShortTimeoutSeconds);
    return queueIds;
}

// Rate plan sequence saving
private async Task ProcessRatePlanSequences(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    var sequences = JsonSerializer.Deserialize<RatePlanSequence[]>(message.MessageAttributes[SQSMessageKeyConstant.RATE_PLAN_SEQUENCES].StringValue);
    var isCommGroupIdParsed = int.TryParse(message.MessageAttributes[SQSMessageKeyConstant.COMM_GROUP_ID].StringValue, out var commGroupId);
    
    if (isCommGroupIdParsed && sequences != null && sequences.Length > 0)
    {
        // Add the rate plans sequences to database
        DataTable dtQueueRatePlan = new DataTable();
        dtQueueRatePlan.Columns.Add(CommonColumnNames.QueueId, typeof(long));
        dtQueueRatePlan.Columns.Add(CommonColumnNames.CommGroupRatePlanId, typeof(long));
        dtQueueRatePlan.Columns.Add(CommonColumnNames.SequenceOrder, typeof(int));
        dtQueueRatePlan.Columns.Add(CommonColumnNames.CreatedBy);
        dtQueueRatePlan.Columns.Add(CommonColumnNames.CreatedDate, typeof(DateTime));

        // Load existing rate plan data
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

// Bulk save operations
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

// Worker triggering
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