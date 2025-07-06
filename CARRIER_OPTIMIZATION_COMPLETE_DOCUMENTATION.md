# Carrier Optimization System - Complete Technical Documentation

## Executive Summary

The Carrier Optimization System is a sophisticated AWS Lambda-based pipeline that minimizes SIM card operational costs through intelligent rate plan optimization. The system processes device populations, analyzes usage patterns, and optimizes rate plan assignments using advanced algorithms across four main Lambda functions.

## System Architecture & Flow

### Core Components
1. **QueueCarrierPlanOptimization** - Orchestrates the entire optimization process
2. **AltaworxJasperAWSGetDevicesQueue** - Synchronizes device data from carrier APIs
3. **AltaworxSimCardCostOptimizer** - Executes optimization algorithms
4. **AltaworxSimCardCostOptimizerCleanup** - Finalizes results and handles cleanup

### Data Flow Overview
```
Trigger → Queue Planning → Device Sync → Data Validation → Rate Pool Generation → 
Queue Creation → Optimization Execution → Result Compilation → Cleanup & Reporting
```

## 1. QueueCarrierPlanOptimization Lambda

### Purpose
Primary orchestrator that initiates and manages the entire optimization process.

### Execution Triggers
- **Scheduled Execution**: CloudWatch Events trigger automatic runs during the last 8 days of billing periods
- **Manual Execution**: SQS messages from AMOP 2.0 for on-demand optimizations
- **Rate Plan Sequences**: Processes pre-generated rate plan sequences

### Core Logic Flow

#### 1.1 Timing Validation
```
Check Current Time → Validate Billing Period → Check Service Provider Settings → 
Verify Optimization Window → Apply Override Logic
```

**Key Validation Rules:**
- Runs during last 8 days of billing cycle
- Honors service provider's optimization start hour
- Allows continuous runs on final day if optimization start hour has passed
- Supports execution override for manual runs

#### 1.2 Session Management
```
Check Running Sessions → Validate Concurrent Executions → Create/Resume Session → 
Initialize Metadata → Set Progress Tracking
```

**Session Logic:**
- Prevents multiple concurrent optimizations per tenant
- Allows re-runs on final day if previous session completed
- Creates optimization session with unique GUID
- Tracks progress for AMOP 2.0 integration

#### 1.3 Device Synchronization Orchestration
```
Validate Sync Requirements → Truncate Staging Tables → Calculate Sync Date → 
Queue Device Sync → Monitor Sync Progress
```

**Sync Strategy:**
- **Full Sync**: Triggered for new sessions, clears staging tables, syncs last 30+ days
- **Incremental Sync**: Continues from last sync point for existing sessions
- **Staging Management**: Uses dedicated staging tables for transaction isolation

#### 1.4 Communication Group Creation
```
Query Device Groups → Validate Rate Plans → Create Communication Groups → 
Assign Devices → Generate Rate Pool Collections
```

**Grouping Logic:**
- Groups devices by `RatePlanIds` field from communication plans
- Validates rate plan eligibility (overage_rate > 0, data_per_overage_charge > 0)
- Creates optimization communication groups for parallel processing
- Enforces 15 rate plan limit per group

#### 1.5 Rate Pool Sequence Generation

**GenerateRatePoolSequences():**
- Creates all possible permutations of rate plans within a rate pool collection
- Orders sequences by cost optimization potential
- Filters out invalid combinations
- Limits sequences to prevent combinatorial explosion

**GenerateRatePoolSequencesByRatePlanTypes():**
- Groups rate plans by type (data, voice, SMS, etc.)
- Generates sequences that maintain plan type diversity
- Applies type-specific optimization rules
- Ensures compatibility across different plan types

**Sequence Characteristics:**
- **Ordering**: Sequences arranged by cost-effectiveness (lowest potential cost first)
- **Filtering**: Eliminates sequences with incompatible plan combinations
- **Limits**: First instance limited by `RATE_PLAN_SEQUENCES_FIRST_INSTANCE_LIMIT`
- **Batching**: Sequences split into batches of `RATE_PLAN_SEQUENCES_BATCH_SIZE`

## 2. AltaworxJasperAWSGetDevicesQueue Lambda

### Purpose
Synchronizes device data and usage information from carrier APIs with comprehensive error handling and retry logic.

### Execution Flow

#### 2.1 Message Processing
```
Receive SQS Message → Parse Attributes → Validate Authentication → 
Process Device List → Handle Errors → Queue Next Step
```

#### 2.2 Device Retrieval Logic
```
Initialize Pagination → Call Jasper API → Validate Response → 
Process Device Batch → Handle Duplicates → Update Staging
```

**API Integration:**
- **Pagination**: Processes devices in pages (configurable size)
- **Rate Limiting**: Implements exponential backoff for API calls
- **Error Handling**: Tracks failed requests, stops after 5 consecutive failures
- **Deduplication**: Removes duplicate ICCIDs within batches

#### 2.3 Data Processing Pipeline
```
Validate Device Data → Enrich with Metadata → Insert to Staging → 
Execute Stored Procedure → Update Master Tables → Queue Next Step
```

**Processing Steps:**
- **Data Validation**: Checks required fields (ICCID, status, rate plan, comm plan)
- **Staging Insert**: Bulk inserts to `JasperDeviceStaging` table
- **Master Update**: Calls `usp_Update_Jasper_Device` stored procedure
- **Integration**: Updates billing cycle metadata and integration settings

#### 2.4 Next Step Routing
Based on `NextStep` parameter:
- **DeviceUsageByRatePlan**: Routes to usage sync queue for optimization
- **DeviceUsageExport**: Routes to export queue for reporting
- **UpdateDeviceRatePlan**: Routes to rate plan update queue

## 3. AltaworxSimCardCostOptimizer Lambda

### Purpose
Executes the core optimization algorithms to find the best rate plan assignments for devices.

### Execution Flow

#### 3.1 Message Processing
```
Receive Queue IDs → Validate Queue Status → Load Instance Data → 
Process Queues → Execute Optimization → Save Results
```

#### 3.2 Optimization Execution Modes

**Standard Processing:**
- Loads all required data for optimization
- Executes full optimization algorithm
- Saves results to database

**Continuation Processing:**
- Resumes from Redis cache for long-running optimizations
- Continues algorithm execution from checkpoint
- Handles Lambda timeout scenarios

#### 3.3 Assignment Strategies

The system executes multiple assignment strategies in sequence:

**Strategy 1: No Grouping + Largest to Smallest**
- Processes devices individually
- Assigns highest usage devices first
- Optimizes for maximum cost reduction

**Strategy 2: No Grouping + Smallest to Largest**
- Processes devices individually
- Assigns lowest usage devices first
- Optimizes for plan utilization

**Strategy 3: Group by Communication Plan + Largest to Smallest**
- Groups devices by communication plan
- Processes high-usage groups first
- Maintains plan consistency

**Strategy 4: Group by Communication Plan + Smallest to Largest**
- Groups devices by communication plan
- Processes low-usage groups first
- Optimizes for bulk assignments

#### 3.4 Cost Calculation Engine
```
Calculate Base Cost → Apply Proration → Calculate Overage → 
Add Regulatory Fees → Apply Taxes → Generate Total Cost
```

**Cost Components:**
- **Base Cost**: Monthly plan cost × (billing days / 30)
- **Overage Cost**: Excess usage × overage rate
- **Regulatory Fees**: Carrier-specific fees
- **Taxes**: Location-based tax calculations

#### 3.5 Result Evaluation
```
Compare Strategy Results → Select Best Assignment → 
Validate Cost Savings → Record Optimization Details → 
Update Queue Status
```

## 4. AltaworxSimCardCostOptimizerCleanup Lambda

### Purpose
Finalizes optimization results, generates reports, and handles post-optimization tasks.

### Execution Flow

#### 4.1 Queue Monitoring
```
Check Queue Depths → Monitor Processing Status → 
Apply Exponential Backoff → Validate Completion
```

**Monitoring Logic:**
- Polls all optimization queues for completion
- Uses exponential backoff (30s → 60s → 120s → max 300s)
- Retries up to 10 times before timeout
- Tracks queue depths and processing status

#### 4.2 Result Compilation
```
Identify Winning Queues → Compile Results → 
Generate Statistics → Create Reports → 
Clean Up Temporary Data
```

**Compilation Process:**
- Selects winning assignment for each communication group
- Compiles cost savings and optimization statistics
- Generates Excel reports with device assignments
- Cleans up non-winning optimization results

#### 4.3 Report Generation

**M2M Reports:**
- Device assignment spreadsheets
- Cost savings summaries
- Rate plan utilization statistics
- Optimization group details

**Mobility Reports:**
- Optimization group summaries
- Device assignment by group
- Cost analysis by carrier

**Cross-Provider Reports:**
- Multi-carrier optimization results
- Provider-specific cost breakdowns
- Consolidated savings reports

#### 4.4 Post-Optimization Tasks

**Rate Plan Updates:**
- Evaluates time remaining in billing cycle
- Estimates update processing time
- Queues automatic rate plan updates if sufficient time
- Sends go/no-go notifications

**Email Notifications:**
- Sends optimization results to stakeholders
- Includes Excel attachments with assignments
- Provides cost savings summaries
- Notifies of any issues or warnings

## Rate Pool Sequence Generation Deep Dive

### GenerateRatePoolSequences() Logic

#### Purpose
Creates optimized sequences of rate plans for assignment testing.

#### Process Flow
```
Input Rate Plans → Filter Compatible Plans → Generate Permutations → 
Apply Optimization Logic → Rank by Cost Potential → Return Sequences
```

#### Key Operations:
1. **Compatibility Check**: Ensures rate plans can be used together
2. **Cost Ranking**: Orders plans by cost-effectiveness
3. **Sequence Generation**: Creates logical assignment sequences
4. **Optimization**: Prioritizes sequences with highest savings potential

### GenerateRatePoolSequencesByRatePlanTypes() Logic

#### Purpose
Generates sequences that maintain diversity across rate plan types.

#### Process Flow
```
Group by Plan Type → Ensure Type Coverage → Generate Balanced Sequences → 
Apply Type-Specific Rules → Optimize for Diversity → Return Sequences
```

#### Key Operations:
1. **Type Classification**: Groups plans by type (data, voice, SMS, etc.)
2. **Diversity Maintenance**: Ensures each sequence covers different plan types
3. **Balanced Generation**: Creates sequences with appropriate type distribution
4. **Type-Specific Optimization**: Applies rules specific to each plan type

### Sequence Characteristics

#### Ordering Principles:
- **Cost Priority**: Lower-cost plans ranked higher
- **Overage Efficiency**: Plans with better overage rates prioritized
- **Usage Alignment**: Plans matched to expected usage patterns
- **Type Diversity**: Sequences include variety of plan types

#### Filtering Criteria:
- **Minimum Overage Rate**: Plans must have overage_rate > 0
- **Valid Data Charges**: Plans must have data_per_overage_charge > 0
- **Compatibility**: Plans must be compatible with service provider
- **Availability**: Plans must be active and available

#### Limits and Constraints:
- **First Instance Limit**: Initial processing limited to prevent timeout
- **Batch Size**: Sequences processed in configurable batches
- **Maximum Permutations**: Prevents combinatorial explosion
- **Time Constraints**: Sequences limited by Lambda execution time

## Assignment Strategy Implementation

### Strategy Selection Logic
```
Portal Type → Customer Type → Optimization Settings → Strategy List
```

**M2M Portal:**
- No Grouping + Largest/Smallest
- Group by Communication Plan + Largest/Smallest

**Mobility Portal:**
- No Grouping only (due to optimization group complexity)

**Cross-Provider:**
- Customer-specific optimization strategies

### Strategy Execution Flow
```
Load Strategy Configuration → Prepare Device Groups → 
Execute Assignment Algorithm → Calculate Costs → 
Evaluate Results → Select Best Strategy
```

### Cost Optimization Logic
```
For each strategy:
  For each device group:
    For each rate plan sequence:
      Calculate assignment cost
      Compare with baseline
      Track best assignment
  Select lowest-cost assignment
Select best strategy result
```

## Database Tables and Data Flow

### Core Tables

**OptimizationSession:**
- Tracks optimization sessions
- Stores session metadata
- Manages session lifecycle

**OptimizationInstance:**
- Represents individual optimization runs
- Contains instance-specific settings
- Tracks execution progress

**OptimizationQueue:**
- Manages optimization work queues
- Stores queue-specific parameters
- Tracks processing status

**JasperDeviceStaging:**
- Temporary storage for device sync
- Isolation layer for data processing
- Supports transaction rollback

### Data Flow Between Components

#### Session Data Flow:
```
AMOP 2.0 → OptimizationSession → OptimizationInstance → OptimizationQueue → 
Device Processing → Results Storage → Report Generation
```

#### Device Data Flow:
```
Carrier API → JasperDeviceStaging → Device Processing → 
OptimizationSimCard → Cost Calculation → ResultRatePool
```

#### Result Data Flow:
```
Optimization Results → Result Compilation → Report Generation → 
Email Delivery → Rate Plan Updates → Cleanup
```

## Error Handling and Recovery

### Error Types and Handling

**API Errors:**
- Exponential backoff with jitter
- Circuit breaker pattern
- Fallback to cached data

**Database Errors:**
- Transaction rollback
- Retry with exponential backoff
- Error logging and alerting

**Lambda Timeouts:**
- Checkpoint to Redis cache
- Resume processing in new instance
- Progress tracking and recovery

**Queue Processing Errors:**
- Dead letter queue routing
- Error notification
- Manual intervention triggers

### Recovery Mechanisms

**Automatic Recovery:**
- Retry logic with backoff
- Checkpoint-based resumption
- Graceful degradation

**Manual Recovery:**
- Error notification emails
- Administrative override capabilities
- Manual queue processing

## Performance Optimization

### Caching Strategy
- **Redis Cache**: Stores intermediate results for large optimizations
- **Device Data Caching**: Reduces database load for repeated queries
- **Rate Plan Caching**: Optimizes sequence generation performance

### Parallel Processing
- **Queue Parallelization**: Multiple optimization queues process concurrently
- **Batch Processing**: Devices processed in optimized batch sizes
- **Lambda Concurrency**: Configurable concurrent execution limits

### Resource Management
- **Memory Optimization**: Efficient data structures for large datasets
- **CPU Optimization**: Algorithmic improvements for cost calculations
- **I/O Optimization**: Batch database operations and connection pooling

## Monitoring and Alerting

### Key Metrics
- **Processing Time**: End-to-end optimization duration
- **Cost Savings**: Percentage and absolute cost reductions
- **Device Count**: Number of devices processed
- **Error Rates**: API and processing error percentages

### Alert Conditions
- **Long Processing Times**: Optimizations exceeding time thresholds
- **High Error Rates**: API or processing errors above limits
- **Queue Depth**: Unusual queue buildup indicating processing issues
- **Cost Anomalies**: Unexpected cost calculation results

### Troubleshooting Capabilities
- **Detailed Logging**: Comprehensive log capture at all levels
- **Progress Tracking**: Real-time optimization progress visibility
- **Error Classification**: Categorized error types for quick resolution
- **Performance Metrics**: Detailed timing and resource usage data

This documentation provides a comprehensive technical overview of the Carrier Optimization process, covering all requested aspects from initialization through cleanup, with detailed explanations of rate pool sequence generation, assignment strategies, and system architecture.