# Continuous Carrier Optimization Process Flow & AMOP 2.0 Trigger Integration

## Executive Summary

The **Continuous Carrier Optimization** system is a sophisticated AWS Lambda-based pipeline that automatically optimizes SIM card rate plans to minimize costs while maintaining service quality. The system integrates with **AMOP 2.0** to provide real-time progress tracking, error notifications, and workflow management through strategic trigger points.

## System Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    Continuous Carrier Optimization Pipeline                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐        │
│  │  CloudWatch     │    │    SQS Queues  │    │   RDS Database  │        │
│  │  Events/Triggers│◄───┤   (Messages)   │◄───┤  (Device Data)  │        │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘        │
│           │                       │                       │                │
│           ▼                       ▼                       ▼                │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐        │
│  │QueueCarrierPlan │    │ DeviceSync      │    │ CostOptimizer   │        │
│  │Optimization     │◄──►│ Lambda          │◄──►│ Lambda          │        │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘        │
│           │                       │                       │                │
│           ▼                       ▼                       ▼                │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐        │
│  │    AMOP 2.0     │    │   Email/SES     │    │ Cleanup Lambda  │        │
│  │  API Triggers   │    │  Notifications  │    │                 │        │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘        │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Detailed Process Flow

### Phase 1: Initialization & Session Management

#### 1.1 Process Trigger
```
CloudWatch Event (Cron) → QueueCarrierPlanOptimization Lambda
```

**Trigger Schedule:**
- **Time-based**: Configurable start times per service provider
- **Monthly Cycle**: Typically runs on the first day of billing period
- **Override**: Manual triggers available for urgent optimizations

#### 1.2 Session Validation
```python
def initialize_optimization_session():
    # Check for running sessions
    if check_running_sessions() and not is_last_day_override():
        return False
    
    # Create optimization session
    session_id = create_optimization_session()
    
    # Send initial progress to AMOP 2.0
    send_amop_trigger("Progress", session_id, 0%, "Session Initialized")
```

**AMOP 2.0 Trigger Point #1:**
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    0, 
    null, 
    0, 
    "", 
    additionalData
);
```

### Phase 2: Device Synchronization

#### 2.1 Device Sync Orchestration
```
QueueCarrierPlanOptimization → SQS Queue → AltaworxJasperAWSGetDevicesQueue
```

**Process Steps:**
1. **Sync Strategy Determination**
   - Check last sync timestamp
   - Decide: Full sync vs. Incremental sync
   - Truncate staging tables if full sync required

2. **Device Data Retrieval**
   - Paginated API calls to carrier (Jasper API)
   - Process 1000 devices per batch
   - Update device staging tables

3. **Data Validation**
   - Validate device status and eligibility
   - Check rate plan associations
   - Verify usage data completeness

**AMOP 2.0 Trigger Point #2:**
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    0, 
    null, 
    20, 
    "", 
    additionalData
);
```

#### 2.2 Communication Grouping
```python
def create_communication_groups(session_id):
    # Group devices by communication plan
    comm_groups = group_devices_by_plan(session_id)
    
    # Validate groups have minimum device count
    valid_groups = validate_group_eligibility(comm_groups)
    
    # Send progress update to AMOP 2.0
    send_amop_trigger("Progress", session_id, 30%, f"Grouped {len(valid_groups)} communication plans")
```

**AMOP 2.0 Trigger Point #3:**
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    deviceCount, 
    null, 
    30, 
    "", 
    additionalData
);
```

### Phase 3: Rate Plan Validation & Optimization Setup

#### 3.1 Rate Plan Validation
```python
def validate_rate_plans(session_id):
    # Check for invalid rate plans
    invalid_plans = check_invalid_rate_plans(session_id)
    
    if invalid_plans:
        # Send error notification to AMOP 2.0
        send_amop_error("One or more Rate Plans have invalid Data per Overage Charge or Overage Rate")
        return False
    
    return True
```

**AMOP 2.0 Error Trigger:**
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "ErrorMessage", 
    optimizationSessionId.ToString(), 
    null, 
    0, 
    "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 
    0, 
    "", 
    additionalData
);
```

#### 3.2 Rate Pool Generation
```python
def generate_rate_pools(comm_group):
    # Calculate rate plan permutations
    rate_plans = get_group_rate_plans(comm_group)
    calculated_plans = RatePoolCalculator.CalculateMaxAvgUsage(rate_plans)
    
    # Create rate pool collections
    rate_pools = RatePoolFactory.CreateRatePools(calculated_plans)
    
    # Send progress update
    send_amop_trigger("Progress", session_id, 40%, "Rate pools generated")
```

**AMOP 2.0 Trigger Point #4:**
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    deviceCount, 
    null, 
    40, 
    "", 
    additionalData
);
```

### Phase 4: Optimization Execution

#### 4.1 Queue Generation & Processing
```python
def execute_optimization(session_id):
    # Generate optimization queues for each permutation
    optimization_queues = generate_optimization_queues(session_id)
    
    # Process each queue through cost optimizer
    for queue in optimization_queues:
        result = AltaworxSimCardCostOptimizer.process(queue)
        store_optimization_result(result)
    
    # Send progress update
    send_amop_trigger("Progress", session_id, 50%, "Optimization processing initiated")
```

**AMOP 2.0 Trigger Point #5:**
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    0, 
    null, 
    50, 
    "", 
    additionalData
);
```

#### 4.2 Cost Calculation & Winner Selection
- **Algorithm Execution**: Calculate cost for each rate plan permutation
- **Result Evaluation**: Compare total costs and savings
- **Winner Selection**: Identify optimal rate plan assignments
- **Validation**: Ensure no service degradation

### Phase 5: Cleanup & Result Processing

#### 5.1 Instance Cleanup
```python
def cleanup_optimization_instances(session_id):
    # Mark instances as complete
    instances = get_session_instances(session_id)
    
    for instance in instances:
        if instance.status == "Processing":
            mark_instance_complete(instance.id)
    
    # Send final progress update
    send_amop_trigger("Progress", session_id, 90%, "Cleanup completed")
```

#### 5.2 Report Generation & Notifications
- **Savings Report**: Generate detailed cost savings analysis
- **Email Notifications**: Send results via SES
- **Rate Plan Updates**: Apply optimized assignments (if enabled)

## AMOP 2.0 Trigger Integration Details

### Trigger Types

#### 1. Progress Triggers
**Purpose**: Real-time progress tracking for optimization workflows

**Structure**:
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context,                    // Lambda context
    "Progress",                 // Message type
    sessionId,                  // Optimization session ID
    sessionGuid,               // Session GUID
    deviceCount,               // Number of devices processed
    null,                      // Error message (null for progress)
    progressPercentage,        // Completion percentage (0-100)
    "",                        // Additional info
    additionalData             // Custom data payload
);
```

**Progress Milestones**:
- 0%: Session initialization
- 20%: Device sync progress
- 30%: Communication grouping complete
- 40%: Rate pool generation complete
- 50%: Optimization processing initiated
- **NO 90%**: Cleanup does NOT send trigger (Missing)
- **NO 100%**: Process completion NOT tracked (Missing)

#### 2. Error Triggers
**Purpose**: Immediate notification of process failures

**Structure**:
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context,                    // Lambda context
    "ErrorMessage",             // Message type
    sessionId,                  // Optimization session ID
    null,                      // Session GUID (null for errors)
    0,                         // Device count (0 for errors)
    errorMessage,              // Detailed error description
    0,                         // Progress percentage (0 for errors)
    "",                        // Additional info
    additionalData             // Error context data
);
```

**Error Scenarios**:
- Invalid rate plan configurations
- Insufficient communication groups
- Rate plan validation failures
- Database connection issues
- API timeout errors

#### 3. Completion Triggers
**Purpose**: Final status notification with results summary

### Data Flow: Optimization → AMOP 2.0

```
┌─────────────────────┐    ┌─────────────────────┐    ┌─────────────────────┐
│ Optimization Lambda │    │ AMOP API Trigger    │    │     AMOP 2.0        │
│                     │    │                     │    │     System          │
├─────────────────────┤    ├─────────────────────┤    ├─────────────────────┤
│                     │    │                     │    │                     │
│ 1. Process Start    │───►│ Format API Call     │───►│ Update Progress     │
│ 2. Progress Update  │───►│ Add Authentication  │───►│ Store Session Data  │
│ 3. Error Handling   │───►│ Send HTTP Request   │───►│ Trigger Workflows   │
│ 4. Completion       │───►│ Log Response        │───►│ Generate Reports    │
│                     │    │                     │    │                     │
└─────────────────────┘    └─────────────────────┘    └─────────────────────┘
```

## System Benefits & Optimization Results

### Cost Savings
- **Average Reduction**: 15-30% on SIM card expenses
- **Processing Scale**: Millions of devices per optimization cycle
- **Automation**: Fully automated monthly optimization

### Operational Efficiency
- **Real-time Tracking**: AMOP 2.0 integration provides live progress updates
- **Error Handling**: Immediate notification and workflow management
- **Scalability**: Handles large device populations efficiently
- **Reliability**: Robust error handling with comprehensive logging

## Monitoring & Alerting

### CloudWatch Integration
- **Lambda Metrics**: Execution duration, error rates, invocation counts
- **Custom Metrics**: Device processing rates, optimization savings
- **Log Analysis**: Structured logging for troubleshooting

### AMOP 2.0 Dashboard
- **Progress Visualization**: Real-time optimization status
- **Error Management**: Centralized error tracking and resolution
- **Workflow Orchestration**: Customer-specific optimization workflows

## Configuration & Management

### Environment Variables
```
AMOP_API_ENDPOINT=https://api.amop20.com/optimization
AMOP_API_KEY=<secure_api_key>
DATABASE_CONNECTION_STRING=<rds_connection>
SQS_QUEUE_URL=<optimization_queue>
```

### Security Considerations
- **API Authentication**: Token-based authentication for AMOP 2.0
- **Data Encryption**: In-transit and at-rest encryption
- **Access Control**: IAM roles and policies for Lambda functions
- **Audit Logging**: Comprehensive audit trail for all operations

---

**Process Owner**: Madhu
**Last Updated**: Current
**System Version**: AMOP 2.0 Integration
**Status**: Production Active