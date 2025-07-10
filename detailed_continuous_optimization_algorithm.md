# DETAILED CONTINUOUS OPTIMIZATION ALGORITHM

## COMPREHENSIVE ALGORITHM BREAKDOWN

### PHASE 1: AUTO-TRIGGERING (QueueCarrierPlanOptimization Lambda)

**PURPOSE**: Initiate optimization process based on scheduling or manual triggers
**LAMBDA**: QueueCarrierPlanOptimization.cs
**TRIGGER SOURCES**: CloudWatch Events, Manual SQS Messages, AMOP 2.0 Requests

#### PHASE 1.1: CloudWatch Event Processing
**WHAT**: Scheduled optimization runs during billing cycle end
**WHEN**: Last 8 days of billing period
**HOW**: CloudWatch Events Rule triggers Lambda

**DETAILED STEPS**:
1. **Event Reception**
   - CloudWatch Event received with tenant/service provider information
   - Extract service provider ID and billing period details
   - Validate event structure and required parameters
   - Log event reception for audit trail

2. **Timing Validation**
   - Calculate current position in billing cycle
   - Check if within last 8 days of billing period
   - Verify service provider optimization start hour settings
   - Apply timezone conversions for accurate timing
   - Allow final day override if optimization start hour passed

3. **Service Provider Configuration**
   - Load service provider optimization settings
   - Check optimization enabled flag
   - Validate optimization window configuration
   - Retrieve carrier-specific parameters
   - Load rate plan filtering rules

#### PHASE 1.2: Session Validation and Management
**WHAT**: Ensure no duplicate optimization sessions running
**WHY**: Prevent resource conflicts and data corruption
**HOW**: Database session checking with locking mechanism

**DETAILED STEPS**:
1. **Concurrent Session Check**
   - Query OptimizationSession table for running sessions
   - Filter by service provider and billing period
   - Check session status (Running, Pending, Processing)
   - Apply session timeout logic for stuck sessions
   - Handle final day override scenarios

2. **Session Creation or Resumption**
   - Generate unique session GUID
   - Create OptimizationSession record with metadata
   - Set session status to 'Initializing'
   - Record session start time and parameters
   - Initialize session progress tracking

3. **Metadata Capture**
   - Record billing period start/end dates
   - Capture service provider configuration
   - Store optimization parameters and settings
   - Log session creation for monitoring
   - Initialize performance metrics tracking

#### PHASE 1.3: Device Synchronization Orchestration
**WHAT**: Coordinate device data sync from carrier APIs
**WHY**: Ensure optimization uses latest device and usage data
**HOW**: Queue device sync jobs with appropriate parameters

**DETAILED STEPS**:
1. **Sync Strategy Determination**
   - Check last successful sync timestamp
   - Calculate data staleness threshold
   - Determine full vs incremental sync requirement
   - Assess API rate limiting considerations
   - Plan sync batch sizing and timing

2. **Staging Table Management**
   - Truncate staging tables for full sync
   - Preserve existing data for incremental sync
   - Initialize staging table indexes
   - Set up transaction isolation
   - Prepare error handling structures

3. **Device Sync Job Queuing**
   - Create SQS message for device sync Lambda
   - Set message attributes with sync parameters
   - Include service provider credentials
   - Specify sync date range and filters
   - Add retry and timeout configurations

#### PHASE 1.4: Communication Group Creation
**WHAT**: Group devices by communication plans for optimization
**WHY**: Enable parallel processing and logical device grouping
**HOW**: Database queries to group devices with validation

**DETAILED STEPS**:
1. **Device Grouping Analysis**
   - Query devices by rate plan IDs
   - Calculate usage statistics per group
   - Determine group sizes and complexity
   - Assess optimization feasibility
   - Identify problem groups requiring special handling

2. **Rate Plan Validation**
   - Verify rate plan overage rates greater than zero
   - Check data per overage charge values
   - Validate rate plan compatibility
   - Ensure rate plans are active and available
   - Report invalid rate plans to AMOP 2.0

3. **Communication Group Creation**
   - Create OptimizationCommGroup records
   - Assign devices to communication groups
   - Set group-specific optimization parameters
   - Initialize group processing status
   - Prepare group-level metrics tracking

#### PHASE 1.5: Rate Pool Sequence Generation
**WHAT**: Create optimized sequences of rate plans for testing
**WHY**: Systematically test rate plan combinations for best results
**HOW**: Algorithmic generation with optimization heuristics

**DETAILED STEPS**:
1. **Rate Plan Collection**
   - Gather all available rate plans for service provider
   - Filter by portal type (M2M, Mobility, Cross-Provider)
   - Apply service provider specific filters
   - Validate plan compatibility and availability
   - Sort by cost effectiveness metrics

2. **Sequence Generation Logic**
   - Generate permutations within rate pool collections
   - Apply ordering by cost optimization potential
   - Filter incompatible plan combinations
   - Limit sequences to prevent combinatorial explosion
   - Optimize for expected usage patterns

3. **Sequence Optimization**
   - Rank sequences by potential cost savings
   - Apply type-specific optimization rules
   - Ensure plan type diversity in sequences
   - Batch sequences for processing efficiency
   - Set processing limits and timeouts

#### PHASE 1.6: Queue Creation and SQS Message Preparation
**WHAT**: Create optimization queues and prepare processing messages
**WHY**: Enable parallel optimization processing across multiple Lambda instances
**HOW**: Database queue creation with SQS message generation

**DETAILED STEPS**:
1. **Optimization Queue Creation**
   - Create OptimizationQueue records for each communication group
   - Set queue processing parameters and limits
   - Initialize queue status and progress tracking
   - Assign rate pool sequences to queues
   - Configure queue-specific optimization settings

2. **SQS Message Generation**
   - Create SQS messages for each optimization queue
   - Set message attributes with queue IDs and parameters
   - Include optimization configuration and flags
   - Set message routing and processing parameters
   - Apply message delay if needed for throttling

3. **Message Dispatch**
   - Send messages to AltaworxSimCardCostOptimizer queue
   - Log message dispatch for tracking
   - Update queue status to 'Queued'
   - Initialize processing timeout monitoring
   - Set up completion tracking mechanisms

### PHASE 2: CONTINUOUS PROCESSING (AltaworxSimCardCostOptimizer Lambda)

**PURPOSE**: Execute optimization algorithms with timeout handling and state persistence
**LAMBDA**: AltaworxSimCardCostOptimizer.cs
**TRIGGER**: SQS messages from Phase 1 or continuation messages

#### PHASE 2.1: Lambda Handler Entry Point
**WHAT**: Initialize Lambda execution context and validate environment
**WHERE**: Handler() method, Lines 40-65
**HOW**: Context setup with Redis connectivity testing

**DETAILED STEPS**:
1. **Context Initialization**
   - Create KeySysLambdaContext from Lambda context
   - Initialize logging and monitoring infrastructure
   - Set up error handling and exception management
   - Configure timeout monitoring and alerting
   - Prepare performance metrics collection

2. **Configuration Loading**
   - Load SanityCheckTimeLimit from environment (default: 180 seconds)
   - Read Redis connection configuration
   - Load database connection strings and timeouts
   - Configure SQS queue URLs and settings
   - Set up optimization algorithm parameters

3. **Redis Connectivity Testing**
   - Test Redis connection using TestRedisConnection()
   - Set IsUsingRedisCache flag based on connection success
   - Handle Redis configuration but unreachable scenarios
   - Log Redis connection status for monitoring
   - Configure fallback behavior for Redis unavailability

4. **Repository Initialization**
   - Initialize database repositories and connections
   - Set up carrier rate plan repository
   - Configure optimization device repositories
   - Prepare cross-provider optimization services
   - Initialize caching and performance helpers

#### PHASE 2.2: Message Processing and Route Detection
**WHAT**: Parse SQS message and determine processing path
**WHERE**: ProcessEventRecord() method, Lines 85-125
**HOW**: Message attribute analysis with routing logic

**DETAILED STEPS**:
1. **Message Validation**
   - Validate SQS message structure and format
   - Extract message ID for tracking and logging
   - Parse message attributes and validate required fields
   - Log message details for debugging and audit
   - Handle malformed or incomplete messages

2. **Attribute Extraction**
   - Extract QueueIds from message attributes
   - Parse SkipLowerCostCheck boolean flag
   - Extract ChargeType enumeration value
   - Read SessionId for tracking and correlation
   - Validate all required attributes present

3. **Processing Path Determination**
   - Check for IsChainingProcess attribute
   - Validate boolean parsing of chaining flag
   - Determine if this is initial or continuation processing
   - Log processing path decision for monitoring
   - Handle invalid or ambiguous routing scenarios

4. **Route Execution**
   - For continuation (IsChainingProcess=true):
     - Validate Redis connection requirement
     - Route to ProcessQueuesContinue() method
     - Log continuation processing initiation
   - For standard processing (IsChainingProcess=false or absent):
     - Route to ProcessQueues() method
     - Log standard processing initiation

#### PHASE 2.3A: Standard Processing Path (Initial Optimization Run)
**WHAT**: Execute full optimization from beginning with data loading
**WHERE**: ProcessQueues() method, Lines 130-270
**HOW**: Complete optimization workflow with timeout monitoring

**DETAILED STEPS**:
1. **Queue and Instance Data Loading**
   - Load optimization queue details from database
   - Validate queue status and processing eligibility
   - Extract instance configuration and settings
   - Load billing period and account information
   - Initialize portal type specific parameters

2. **Data Validation and Status Checking**
   - Check queue status against FINISHED_STATUSES list
   - Prevent duplicate processing of completed queues
   - Validate instance configuration completeness
   - Ensure all required data available for optimization
   - Handle invalid or corrupted queue data

3. **SimCards Data Loading with Caching**
   - Check Redis cache availability for SimCards data
   - If Redis available: Use GetSimCardsFromCache() with fallback
   - If Redis unavailable: Load directly from GetSimCardsByPortalType()
   - Handle different portal types (M2M, Mobility, Cross-Provider)
   - Validate device data completeness and quality

4. **Rate Pool Collection Creation**
   - Load rate plans for optimization queues
   - Calculate rate plan sequences and ordering
   - Create RatePoolCollection with optimization parameters
   - Configure pooling behavior based on portal type
   - Set up rate plan filtering and validation rules

5. **Timeout Monitoring Setup**
   - Monitor Lambda remaining execution time
   - Calculate remainingSeconds from context.LambdaContext.RemainingTime
   - Log remaining time for performance monitoring
   - Set up proactive timeout handling
   - Configure early completion triggers

6. **Optimization Engine Creation**
   - Create RatePoolAssigner with all required parameters
   - Pass ratePoolCollection, simCards, logger, SanityCheckTimeLimit
   - Include LambdaContext for timeout monitoring
   - Set IsUsingRedisCache flag for state persistence capability
   - Configure portal-specific optimization behavior

7. **Algorithm Execution**
   - Execute assigner.AssignSimCards() with strategy parameters
   - Use GetSimCardGroupingByPortalType() for grouping strategy
   - Apply billing timezone for cost calculations
   - Execute multiple assignment strategies in sequence
   - Monitor progress and handle intermediate timeouts

8. **Completion Handling**
   - Call WrapUpCurrentInstance() for completion decision logic
   - Pass all required parameters for result processing
   - Handle both completion and continuation scenarios
   - Update queue status and progress tracking
   - Prepare for next phase processing

#### PHASE 2.3B: Continuation Processing Path (Resume from Checkpoint)
**WHAT**: Resume optimization from saved Redis state
**WHERE**: ProcessQueuesContinue() method, Lines 290-365
**HOW**: State restoration with optimization resumption

**DETAILED STEPS**:
1. **Input Parameter Validation**
   - Validate queueIds list not empty
   - Check message attributes completeness
   - Verify Redis connection availability requirement
   - Log continuation processing parameters
   - Handle invalid continuation requests

2. **Queue Status Validation**
   - Load reference queue for status checking
   - Verify queue not in FINISHED_STATUSES
   - Prevent duplicate continuation processing
   - Handle race conditions from SQS at-least-once delivery
   - Log queue status validation results

3. **Instance Metadata Loading**
   - Load instance configuration from reference queue
   - Extract AMOP customer ID and account information
   - Load communication plan group details
   - Prepare customer identification for result recording
   - Handle missing or invalid instance data

4. **Redis State Restoration**
   - Call RedisCacheHelper.GetPartialAssignerFromCache()
   - Pass queueIds and billing timezone for state lookup
   - Handle cache miss scenarios (consider optimization complete)
   - Validate restored state integrity and completeness
   - Log state restoration success or failure

5. **Optimization Context Restoration**
   - Set Lambda context on restored assigner
   - Configure logger for continued processing
   - Restore timeout monitoring configuration
   - Re-establish Redis connection if needed
   - Prepare for algorithm continuation

6. **Algorithm Resumption**
   - Call assigner.AssignSimCardsContinue() to resume processing
   - Continue from saved checkpoint in optimization algorithm
   - Apply same billing timezone and parameters
   - Monitor progress and handle completion detection
   - Handle any resumption errors or issues

7. **Completion Processing**
   - Call WrapUpCurrentInstance() for final processing
   - Handle completion or further continuation needs
   - Update progress tracking and status
   - Clean up continuation-specific resources
   - Prepare for result processing or next continuation

#### PHASE 2.4: Optimization Algorithm Execution Details
**WHAT**: Execute multiple assignment strategies with cost optimization
**HOW**: Sequential strategy execution with best result selection

**DETAILED ALGORITHM STRATEGIES**:
1. **Strategy 1: No Grouping + Largest to Smallest**
   - Process devices individually without grouping
   - Sort devices by usage from highest to lowest
   - Assign high-usage devices to most cost-effective plans
   - Optimize for maximum cost reduction potential
   - Handle device-specific optimization constraints

2. **Strategy 2: No Grouping + Smallest to Largest**
   - Process devices individually without grouping
   - Sort devices by usage from lowest to highest
   - Assign low-usage devices first for plan utilization
   - Optimize for overall plan efficiency
   - Balance cost reduction with plan utilization

3. **Strategy 3: Group by Communication Plan + Largest to Smallest**
   - Group devices by communication plan assignments
   - Process groups ordered by total usage (high to low)
   - Maintain communication plan consistency
   - Optimize for group-level cost effectiveness
   - Handle group-specific constraints and requirements

4. **Strategy 4: Group by Communication Plan + Smallest to Largest**
   - Group devices by communication plan assignments
   - Process groups ordered by total usage (low to high)
   - Optimize for bulk assignment efficiency
   - Balance group consistency with cost optimization
   - Handle low-usage group optimization challenges

**COST CALCULATION ENGINE**:
1. **Base Cost Calculation**
   - Monthly plan cost prorated by billing period days
   - Formula: planCost × (billingDays / 30)
   - Handle different billing cycle lengths
   - Apply proration rules consistently
   - Account for partial month scenarios

2. **Overage Cost Calculation**
   - Calculate excess usage beyond plan included data
   - Determine overage blocks needed
   - Formula: Math.Ceiling(overageUsage / dataPerOverageCharge) × overageRate
   - Handle different overage charging models
   - Apply carrier-specific overage rules

3. **Additional Fees and Taxes**
   - Calculate regulatory fees per device and plan
   - Apply location-based tax calculations
   - Include carrier-specific surcharges
   - Handle tax exemptions and special rates
   - Ensure compliance with regulatory requirements

4. **Total Cost Aggregation**
   - Sum base cost, overage cost, fees, and taxes
   - Provide detailed cost breakdown per device
   - Calculate group and session level totals
   - Track cost savings and optimization effectiveness
   - Generate cost comparison reports

### PHASE 3: CHAINING MECHANISM (Within AltaworxSimCardCostOptimizer)

**PURPOSE**: Handle optimization continuation when Lambda timeout approaches
**LOCATION**: WrapUpCurrentInstance() method, Lines 365-400
**MECHANISM**: Redis state persistence with SQS message chaining

#### PHASE 3.1: Completion Status Evaluation
**WHAT**: Determine if optimization is complete or needs continuation
**HOW**: Check assigner.IsCompleted flag with Redis availability

**DETAILED DECISION LOGIC**:
1. **Completion Flag Analysis**
   - Check assigner.IsCompleted boolean status
   - Evaluate optimization algorithm progress
   - Assess remaining work and time requirements
   - Consider strategy execution completeness
   - Handle partial completion scenarios

2. **Redis Availability Assessment**
   - Verify context.IsRedisConnectionStringValid flag
   - Check IsUsingRedisCache current status
   - Assess Redis connection health and performance
   - Handle Redis connectivity issues
   - Plan fallback behavior for Redis unavailability

3. **Continuation Feasibility Check**
   - Evaluate remaining Lambda execution time
   - Assess state serialization feasibility
   - Check queue processing requirements
   - Validate continuation message requirements
   - Handle edge cases and error scenarios

#### PHASE 3.2: State Persistence to Redis (For Incomplete Optimizations)
**WHAT**: Save current optimization state for later resumption
**HOW**: RedisCacheHelper.RecordPartialAssignerToCache() operation

**DETAILED SERIALIZATION PROCESS**:
1. **State Extraction**
   - Extract current RatePoolAssigner state
   - Capture processed device indices and assignments
   - Record current strategy execution progress
   - Save intermediate optimization results
   - Include rate pool collections and metadata

2. **Redis Key Generation**
   - Create unique key: "optimization_state:{sessionId}:{queueIds}"
   - Sort queue IDs for consistent key generation
   - Include session identifier for isolation
   - Handle key collisions and overwrites
   - Log key generation for debugging

3. **Object Serialization**
   - Serialize RatePoolAssigner using JsonConvert
   - Apply compression for large state objects
   - Handle circular references and complex objects
   - Validate serialization success and completeness
   - Prepare for efficient deserialization

4. **Redis Storage Operation**
   - Store serialized state with TTL (3600 seconds)
   - Use SetStringAsync for async operation
   - Handle Redis write failures and retries
   - Log storage operation success or failure
   - Clean up resources after storage

5. **Remaining Queue Extraction**
   - Identify unprocessed queue IDs from assigner state
   - Filter out completed or failed queues
   - Validate remaining work requirements
   - Return null if no continuation needed
   - Prepare queue list for continuation message

#### PHASE 3.3: SQS Continuation Message Creation
**WHAT**: Create and send continuation message to trigger next Lambda
**HOW**: EnqueueOptimizationContinueProcessAsync() method

**DETAILED MESSAGE CREATION**:
1. **Message Body Construction**
   - Create structured message body with action type
   - Include session ID for tracking and correlation
   - Add timestamp for processing timeline
   - Include original message ID for audit trail
   - Serialize message body to JSON format

2. **Critical Message Attributes Setup**
   - QueueIds: Comma-separated remaining queue IDs
   - IsChainingProcess: "true" (KEY FLAG for routing)
   - SkipLowerCostCheck: Preserve original optimization parameter
   - ChargeType: Preserve original charge calculation type
   - SessionId: Optimization session identifier
   - ContinuationAttempt: Track continuation chain length

3. **Message Timing and Delay**
   - Calculate appropriate message delay if needed
   - Apply exponential backoff for retry scenarios
   - Handle immediate processing requirements
   - Consider Lambda cold start times
   - Optimize for processing continuity

4. **SQS Send Operation**
   - Send message to optimization queue
   - Handle SQS send failures and retries
   - Log message dispatch success and message ID
   - Update processing metrics and counters
   - Monitor message delivery and processing

#### PHASE 3.4: Lambda Instance Chaining
**WHAT**: Trigger new Lambda instance to continue optimization
**HOW**: SQS message consumption by new Lambda instance

**CHAINING PROCESS**:
1. **Current Lambda Completion**
   - Log successful continuation setup
   - Update processing metrics and status
   - Clean up current Lambda resources
   - Return from current execution context
   - Allow Lambda to terminate normally

2. **New Lambda Triggering**
   - SQS automatically triggers new Lambda instance
   - New instance receives continuation message
   - Message routing detects IsChainingProcess=true
   - Routes to ProcessQueuesContinue() method
   - Loads saved state from Redis cache

3. **State Restoration and Resumption**
   - Restore optimization state from Redis
   - Re-establish optimization context and parameters
   - Resume algorithm execution from checkpoint
   - Continue with same optimization strategies
   - Maintain processing continuity and integrity

4. **Iteration and Loop Control**
   - Process continues until optimization complete
   - Each continuation follows same chaining pattern
   - Monitor total chain length and execution time
   - Handle chain length limits and timeouts
   - Ensure eventual completion or graceful failure

### PHASE 4: COMPLETION (AltaworxSimCardCostOptimizerCleanup Lambda)

**PURPOSE**: Finalize optimization results and handle post-processing tasks
**LAMBDA**: AltaworxSimCardCostOptimizerCleanup.cs
**TRIGGER**: All optimization queues completed or timeout

#### PHASE 4.1: Queue Monitoring and Completion Detection
**WHAT**: Monitor all optimization queues for completion
**HOW**: Exponential backoff polling with timeout handling

**DETAILED MONITORING PROCESS**:
1. **Queue Depth Monitoring**
   - Query all optimization queues for current depths
   - Check SQS message counts and processing status
   - Monitor optimization queue completion rates
   - Track queue processing performance metrics
   - Handle queue stalling and timeout scenarios

2. **Exponential Backoff Implementation**
   - Start with 30-second polling interval
   - Increase intervals: 30s → 60s → 120s → 300s (max)
   - Retry up to 10 times before timeout
   - Handle polling failures and service outages
   - Log polling attempts and results

3. **Completion Criteria Validation**
   - All queues report zero message depth
   - All optimization queues marked as completed
   - No pending or processing queue statuses
   - Validate completion timestamps and duration
   - Handle partial completion scenarios

4. **Timeout and Error Handling**
   - Handle polling timeout after maximum retries
   - Process partial completion scenarios
   - Generate timeout alerts and notifications
   - Handle service outages and dependencies
   - Ensure graceful degradation and recovery

#### PHASE 4.2: Result Compilation and Aggregation
**WHAT**: Compile optimization results from all communication groups
**HOW**: Database queries with result aggregation and validation

**DETAILED COMPILATION PROCESS**:
1. **Winning Assignment Identification**
   - Query OptimizationGroups for winning assignments
   - Select best cost assignment per communication group
   - Validate winning assignment completeness and accuracy
   - Handle multiple winners or tie scenarios
   - Ensure result consistency and integrity

2. **Cost Savings Calculation**
   - Calculate baseline costs vs optimized costs
   - Aggregate savings across all communication groups
   - Compute percentage savings and effectiveness metrics
   - Handle negative savings and cost increase scenarios
   - Validate calculation accuracy and consistency

3. **Statistics Generation**
   - Generate device count and assignment statistics
   - Calculate rate plan utilization metrics
   - Compute optimization effectiveness indicators
   - Track strategy performance and success rates
   - Create summary metrics for reporting

4. **Data Validation and Quality Checks**
   - Validate result completeness and accuracy
   - Check for data inconsistencies or errors
   - Verify calculation correctness and logic
   - Handle missing or corrupted result data
   - Ensure data integrity for reporting

#### PHASE 4.3: Report Generation and Output Creation
**WHAT**: Generate Excel reports and summary documents
**HOW**: Excel file creation with device assignments and cost analysis

**DETAILED REPORT GENERATION**:
1. **M2M Portal Reports**
   - Device assignment spreadsheets with detailed assignments
   - Cost savings summaries with breakdown by group
   - Rate plan utilization statistics and analysis
   - Optimization group details and performance metrics
   - Device-level assignment recommendations

2. **Mobility Portal Reports**
   - Optimization group summaries with cost analysis
   - Device assignment by optimization group
   - Cost analysis by carrier and rate plan
   - Group-level optimization recommendations
   - Performance metrics and effectiveness analysis

3. **Cross-Provider Reports**
   - Cross-carrier optimization analysis
   - Provider comparison and recommendations
   - Cost effectiveness by carrier
   - Migration recommendations and impact analysis
   - Multi-carrier optimization strategies

4. **Excel File Creation**
   - Create structured Excel workbooks with multiple sheets
   - Format data for readability and analysis
   - Include charts and visualizations where appropriate
   - Apply conditional formatting for key metrics
   - Prepare files for email attachment and distribution

#### PHASE 4.4: Post-Optimization Task Processing
**WHAT**: Handle rate plan updates and notification delivery
**HOW**: Conditional processing based on time and configuration

**DETAILED POST-PROCESSING**:
1. **Rate Plan Update Evaluation**
   - Calculate remaining time in billing cycle
   - Estimate rate plan update processing time
   - Assess feasibility of automatic updates
   - Check update buffer time requirements
   - Handle update batch size limitations

2. **Automatic Update Decision Logic**
   - If sufficient time remaining: Queue automatic updates
   - If insufficient time: Send manual update notifications
   - Consider update complexity and risk factors
   - Apply conservative timing buffers
   - Handle carrier-specific update requirements

3. **Email Notification Generation**
   - Prepare optimization results summary emails
   - Attach Excel reports and detailed analysis
   - Include cost savings summaries and recommendations
   - Add any warnings or issues encountered
   - Format for stakeholder consumption and action

4. **Stakeholder Communication**
   - Send results to configured email recipients
   - Include appropriate level of detail per recipient
   - Provide actionable recommendations and next steps
   - Handle delivery failures and retry logic
   - Log communication delivery and status

#### PHASE 4.5: Cleanup and Resource Management
**WHAT**: Clean up temporary data and resources
**HOW**: Database cleanup with resource deallocation

**DETAILED CLEANUP PROCESS**:
1. **Temporary Data Cleanup**
   - Remove non-winning optimization results
   - Clean up staging tables and temporary data
   - Archive completed session data if required
   - Free database resources and connections
   - Handle cleanup failures and partial cleanup

2. **Redis Cache Cleanup**
   - Remove cached device data and optimization states
   - Clean up session-specific cache entries
   - Free Redis memory and resources
   - Handle cache cleanup failures gracefully
   - Monitor cache usage and performance

3. **Session Status Finalization**
   - Update optimization session status to completed
   - Record final completion timestamp and metrics
   - Archive session logs and audit trail
   - Update session performance metrics
   - Handle session cleanup and resource deallocation

4. **Monitoring and Alerting**
   - Send completion notifications to monitoring systems
   - Update performance dashboards and metrics
   - Generate alerts for any issues or failures
   - Archive logs and audit trail data
   - Prepare for next optimization cycle

This detailed algorithm provides comprehensive coverage of the entire continuous optimization process, from initial triggering through final cleanup, with detailed steps, error handling, and resource management at each phase.