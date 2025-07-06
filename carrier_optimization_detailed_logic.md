# Carrier Optimization Process - Detailed Logic Documentation

## Table of Contents
1. [Executive Summary](#executive-summary)
2. [System Architecture](#system-architecture)
3. [Process Flow Overview](#process-flow-overview)
4. [Detailed Lambda Function Logic](#detailed-lambda-function-logic)
5. [Database Operations](#database-operations)
6. [Algorithm Implementation](#algorithm-implementation)
7. [Error Handling and Recovery](#error-handling-and-recovery)
8. [Performance Optimization](#performance-optimization)
9. [Monitoring and Alerting](#monitoring-and-alerting)
10. [Configuration Management](#configuration-management)
11. [Troubleshooting Guide](#troubleshooting-guide)

## Executive Summary

The Carrier Optimization Process is a sophisticated, multi-stage AWS Lambda-based pipeline designed to minimize SIM card operational costs while maintaining service quality. The system processes millions of devices across multiple carriers, analyzing usage patterns and optimizing rate plan assignments through advanced algorithms.

### Key Objectives
- **Cost Reduction**: Minimize monthly SIM card costs through optimal rate plan assignments
- **Service Quality**: Ensure no service degradation during optimization
- **Scalability**: Handle large-scale device populations efficiently
- **Reliability**: Maintain high availability with robust error handling
- **Transparency**: Provide detailed reporting and audit trails

### System Benefits
- Average 15-30% cost reduction on SIM card expenses
- Automated monthly optimization cycles
- Real-time device sync with carrier APIs
- Comprehensive reporting and analytics
- Scalable architecture supporting millions of devices

## System Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Carrier Optimization System                        │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐        │
│  │  CloudWatch     │    │    SQS Queues  │    │   RDS Database  │        │
│  │  Events/Logs    │    │                 │    │                 │        │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘        │
│           │                       │                       │                │
│           │                       │                       │                │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐        │
│  │ Lambda Function │    │ Lambda Function │    │ Lambda Function │        │
│  │ QueueCarrier    │    │ DeviceSync      │    │ CostOptimizer   │        │
│  │ PlanOptimization│    │ Queue           │    │                 │        │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘        │
│           │                       │                       │                │
│           │                       │                       │                │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐        │
│  │ Redis Cache     │    │ S3 Storage      │    │ Lambda Function │        │
│  │                 │    │                 │    │ Cleanup         │        │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘        │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Component Interactions

1. **Event Triggers**: CloudWatch Events trigger the initial optimization process
2. **Queue Management**: SQS queues manage inter-Lambda communication
3. **Data Persistence**: RDS stores optimization results and device data
4. **Caching**: Redis provides high-performance caching for large datasets
5. **Storage**: S3 stores generated reports and backup data
6. **Monitoring**: CloudWatch provides comprehensive logging and metrics

## Process Flow Overview

### Phase 1: Initialization and Validation
```
Start → Check Running Sessions → Validate Billing Period → Create Session → Sync Device Data
```

### Phase 2: Data Preparation
```
Device Sync → Data Validation → Communication Grouping → Rate Plan Validation → Rate Pool Generation
```

### Phase 3: Optimization Execution
```
Queue Generation → Batch Processing → Algorithm Execution → Result Evaluation → Winner Selection
```

### Phase 4: Cleanup and Reporting
```
Instance Cleanup → Result Compilation → Report Generation → Email Notifications → Rate Plan Updates
```

## Detailed Lambda Function Logic

### 1. QueueCarrierPlanOptimization Lambda

#### 1.1 Initialization Logic

```python
def initialize_optimization_session():
    """
    Initialize optimization session with comprehensive validation
    """
    # Step 1: Validate execution timing
    if not validate_execution_timing():
        return False
    
    # Step 2: Check for running sessions
    running_sessions = check_running_sessions()
    if running_sessions and not is_last_day_override():
        log_warning("Optimization already running", running_sessions)
        return False
    
    # Step 3: Create or retrieve session
    session_id = create_optimization_session()
    
    # Step 4: Initialize metadata
    capture_session_metadata(session_id)
    
    return session_id
```

#### 1.2 Device Sync Orchestration

```python
def orchestrate_device_sync(session_id, service_provider_id):
    """
    Orchestrate device synchronization with carrier APIs
    """
    # Check last sync timestamp
    last_sync = get_last_sync_timestamp(service_provider_id)
    
    # Determine sync strategy
    if should_full_sync(last_sync):
        # Full sync: Truncate staging tables
        truncate_staging_tables(service_provider_id)
        sync_start_date = calculate_full_sync_date()
    else:
        # Incremental sync: From last sync point
        sync_start_date = last_sync
    
    # Queue device sync job
    queue_device_sync_job({
        'service_provider_id': service_provider_id,
        'session_id': session_id,
        'sync_start_date': sync_start_date,
        'full_sync': should_full_sync(last_sync)
    })
```

#### 1.3 Communication Grouping Logic

```python
def create_communication_groups(session_id):
    """
    Group devices by communication plans for optimization
    """
    # Query devices with same rate plan IDs
    device_groups = db.execute("""
        SELECT rate_plan_id, COUNT(*) as device_count,
               MIN(usage_mb) as min_usage, MAX(usage_mb) as max_usage,
               AVG(usage_mb) as avg_usage
        FROM devices 
        WHERE session_id = %s 
        GROUP BY rate_plan_id
    """, (session_id,))
    
    for group in device_groups:
        # Create optimization communication group
        group_id = create_optimization_comm_group({
            'session_id': session_id,
            'rate_plan_id': group['rate_plan_id'],
            'device_count': group['device_count'],
            'usage_stats': {
                'min': group['min_usage'],
                'max': group['max_usage'],
                'avg': group['avg_usage']
            }
        })
        
        # Assign devices to group
        assign_devices_to_group(group_id, group['rate_plan_id'])
```

#### 1.4 Rate Plan Validation

```python
def validate_rate_plans(session_id):
    """
    Validate rate plans for optimization eligibility
    """
    invalid_plans = db.execute("""
        SELECT rate_plan_id, name, overage_rate, data_per_overage_charge
        FROM rate_plans rp
        JOIN session_devices sd ON rp.id = sd.rate_plan_id
        WHERE sd.session_id = %s
        AND (rp.overage_rate <= 0 OR rp.data_per_overage_charge <= 0)
    """, (session_id,))
    
    if invalid_plans:
        # Notify AMOP 2.0 of invalid plans
        notify_amop_invalid_plans(session_id, invalid_plans)
        
        # Mark session as failed
        update_session_status(session_id, 'FAILED', 
                            f"Invalid rate plans: {[p['name'] for p in invalid_plans]}")
        return False
    
    return True
```

### 2. AltaworxJasperAWSGetDevicesQueue Lambda

#### 2.1 Device Retrieval Logic

```python
def retrieve_devices_paginated(service_provider_id, start_date, page_size=1000):
    """
    Retrieve devices using paginated Jasper API calls
    """
    page_number = 1
    total_devices = 0
    
    while True:
        try:
            # Call Jasper API with pagination
            response = jasper_api_client.get_devices(
                service_provider_id=service_provider_id,
                since_date=start_date,
                page_size=page_size,
                page_number=page_number
            )
            
            devices = response.get('devices', [])
            
            if not devices:
                break
                
            # Process batch
            processed_count = process_device_batch(devices)
            total_devices += processed_count
            
            # Check if last page
            if len(devices) < page_size:
                break
                
            page_number += 1
            
        except Exception as e:
            handle_api_error(e, page_number)
            break
    
    return total_devices
```

#### 2.2 Data Validation and Processing

```python
def process_device_batch(devices):
    """
    Process and validate device data batch
    """
    valid_devices = []
    duplicate_iccids = set()
    
    for device in devices:
        # Validate required fields
        if not validate_device_data(device):
            log_warning(f"Invalid device data: {device.get('iccid', 'unknown')}")
            continue
        
        # Check for duplicates
        iccid = device['iccid']
        if iccid in duplicate_iccids:
            log_warning(f"Duplicate ICCID in batch: {iccid}")
            continue
        
        duplicate_iccids.add(iccid)
        
        # Enrich device data
        enriched_device = enrich_device_data(device)
        valid_devices.append(enriched_device)
    
    # Bulk insert to staging table
    if valid_devices:
        bulk_insert_staging(valid_devices)
        
        # Call stored procedure for final processing
        for device in valid_devices:
            call_update_jasper_device_sp(device)
    
    return len(valid_devices)
```

#### 2.3 Usage Data Calculation

```python
def calculate_usage_metrics(device_data):
    """
    Calculate comprehensive usage metrics for optimization
    """
    usage_history = device_data.get('usage_history', [])
    
    if not usage_history:
        return None
    
    # Calculate various usage metrics
    usage_values = [u['data_usage_mb'] for u in usage_history]
    
    metrics = {
        'total_usage_mb': sum(usage_values),
        'average_usage_mb': sum(usage_values) / len(usage_values),
        'max_usage_mb': max(usage_values),
        'min_usage_mb': min(usage_values),
        'std_deviation': calculate_std_deviation(usage_values),
        'percentile_95': calculate_percentile(usage_values, 95),
        'percentile_80': calculate_percentile(usage_values, 80),
        'usage_trend': calculate_usage_trend(usage_values),
        'days_with_usage': len([u for u in usage_values if u > 0])
    }
    
    return metrics
```

### 3. AltaworxSimCardCostOptimizer Lambda

#### 3.1 Queue Processing Logic

```python
def process_optimization_queue(queue_url, batch_size=10):
    """
    Process optimization queue with intelligent batching
    """
    while True:
        # Receive messages from queue
        messages = sqs_client.receive_messages(
            QueueUrl=queue_url,
            MaxNumberOfMessages=batch_size,
            WaitTimeSeconds=20
        )
        
        if not messages:
            break
            
        # Process messages in parallel
        with ThreadPoolExecutor(max_workers=batch_size) as executor:
            futures = []
            
            for message in messages:
                future = executor.submit(process_optimization_message, message)
                futures.append(future)
            
            # Wait for all to complete
            for future in futures:
                try:
                    result = future.result(timeout=300)  # 5 minute timeout
                    if result:
                        log_success(f"Optimization completed: {result}")
                except Exception as e:
                    log_error(f"Optimization failed: {e}")
```

#### 3.2 Assignment Algorithm Implementation

```python
def execute_optimization_algorithm(communication_group, rate_plans):
    """
    Execute optimization algorithm with multiple strategies
    """
    strategies = [
        'no_grouping_largest_first',
        'no_grouping_smallest_first', 
        'grouped_by_plan_largest_first',
        'grouped_by_plan_smallest_first'
    ]
    
    best_assignment = None
    best_cost = float('inf')
    
    for strategy in strategies:
        try:
            # Execute strategy
            assignment = execute_strategy(strategy, communication_group, rate_plans)
            
            # Calculate total cost
            total_cost = calculate_total_cost(assignment, rate_plans)
            
            # Track if best so far
            if total_cost < best_cost:
                best_cost = total_cost
                best_assignment = assignment
                
        except Exception as e:
            log_error(f"Strategy {strategy} failed: {e}")
            continue
    
    return best_assignment, best_cost
```

#### 3.3 Cost Calculation Logic

```python
def calculate_assignment_cost(devices, rate_plan, billing_period_days):
    """
    Calculate precise cost for device assignment to rate plan
    """
    total_cost = 0
    
    for device in devices:
        # Base monthly cost (prorated)
        base_cost = rate_plan['monthly_cost'] * (billing_period_days / 30)
        
        # Calculate overage cost
        included_data = rate_plan['included_data_mb']
        device_usage = device['projected_usage_mb']
        
        overage_cost = 0
        if device_usage > included_data:
            overage_mb = device_usage - included_data
            overage_blocks = math.ceil(overage_mb / rate_plan['data_per_overage_charge'])
            overage_cost = overage_blocks * rate_plan['overage_rate']
        
        # Add regulatory fees and taxes
        regulatory_fees = calculate_regulatory_fees(device, rate_plan)
        taxes = calculate_taxes(base_cost + overage_cost, device['location'])
        
        device_total_cost = base_cost + overage_cost + regulatory_fees + taxes
        total_cost += device_total_cost
        
        # Store detailed cost breakdown
        device['cost_breakdown'] = {
            'base_cost': base_cost,
            'overage_cost': overage_cost,
            'regulatory_fees': regulatory_fees,
            'taxes': taxes,
            'total_cost': device_total_cost
        }
    
    return total_cost
```

### 4. AltaworxSimCardCostOptimizerCleanup Lambda

#### 4.1 Queue Monitoring Logic

```python
def monitor_optimization_queues(session_id):
    """
    Monitor optimization queues with exponential backoff
    """
    max_retries = 10
    retry_count = 0
    base_delay = 30  # seconds
    
    while retry_count < max_retries:
        try:
            # Check queue depths
            queue_status = check_all_queue_depths(session_id)
            
            if all(status['depth'] == 0 for status in queue_status.values()):
                log_info("All queues empty, proceeding with cleanup")
                return True
            
            # Log queue status
            for queue_name, status in queue_status.items():
                log_info(f"Queue {queue_name}: {status['depth']} messages pending")
            
            # Wait with exponential backoff
            delay = base_delay * (2 ** retry_count)
            time.sleep(min(delay, 300))  # Cap at 5 minutes
            
            retry_count += 1
            
        except Exception as e:
            log_error(f"Error monitoring queues: {e}")
            retry_count += 1
    
    # Timeout exceeded
    log_error("Queue monitoring timeout exceeded")
    return False
```

#### 4.2 Result Compilation Logic

```python
def compile_optimization_results(session_id):
    """
    Compile comprehensive optimization results
    """
    # Get winning assignments
    winning_assignments = db.execute("""
        SELECT og.group_id, og.winning_rate_plan_id, 
               og.baseline_cost, og.optimized_cost,
               og.cost_savings, og.device_count
        FROM optimization_groups og
        WHERE og.session_id = %s
        AND og.is_winner = true
    """, (session_id,))
    
    # Calculate summary statistics
    summary = {
        'total_devices': sum(a['device_count'] for a in winning_assignments),
        'total_baseline_cost': sum(a['baseline_cost'] for a in winning_assignments),
        'total_optimized_cost': sum(a['optimized_cost'] for a in winning_assignments),
        'total_savings': sum(a['cost_savings'] for a in winning_assignments),
        'average_savings_percent': 0
    }
    
    if summary['total_baseline_cost'] > 0:
        summary['average_savings_percent'] = (
            summary['total_savings'] / summary['total_baseline_cost'] * 100
        )
    
    # Get detailed device assignments
    device_assignments = db.execute("""
        SELECT d.iccid, d.current_rate_plan_id, d.recommended_rate_plan_id,
               d.current_cost, d.recommended_cost, d.cost_savings,
               d.usage_mb, d.projected_usage_mb
        FROM device_assignments d
        WHERE d.session_id = %s
        ORDER BY d.cost_savings DESC
    """, (session_id,))
    
    return {
        'summary': summary,
        'winning_assignments': winning_assignments,
        'device_assignments': device_assignments
    }
```

## Database Operations

### Key Database Tables

#### 1. optimization_sessions
```sql
CREATE TABLE optimization_sessions (
    session_id UUID PRIMARY KEY,
    service_provider_id INTEGER NOT NULL,
    tenant_id INTEGER NOT NULL,
    billing_period_id INTEGER NOT NULL,
    status VARCHAR(50) DEFAULT 'PENDING',
    device_count INTEGER,
    baseline_cost DECIMAL(10,2),
    optimized_cost DECIMAL(10,2),
    cost_savings DECIMAL(10,2),
    savings_percent DECIMAL(5,2),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    completed_at TIMESTAMP NULL
);
```

#### 2. optimization_groups
```sql
CREATE TABLE optimization_groups (
    group_id UUID PRIMARY KEY,
    session_id UUID REFERENCES optimization_sessions(session_id),
    communication_group_id INTEGER,
    rate_plan_id INTEGER,
    device_count INTEGER,
    baseline_cost DECIMAL(10,2),
    optimized_cost DECIMAL(10,2),
    cost_savings DECIMAL(10,2),
    winning_rate_plan_id INTEGER,
    is_winner BOOLEAN DEFAULT FALSE,
    algorithm_used VARCHAR(100),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

#### 3. device_assignments
```sql
CREATE TABLE device_assignments (
    assignment_id UUID PRIMARY KEY,
    session_id UUID REFERENCES optimization_sessions(session_id),
    iccid VARCHAR(50) NOT NULL,
    current_rate_plan_id INTEGER,
    recommended_rate_plan_id INTEGER,
    current_cost DECIMAL(8,2),
    recommended_cost DECIMAL(8,2),
    cost_savings DECIMAL(8,2),
    usage_mb INTEGER,
    projected_usage_mb INTEGER,
    confidence_score DECIMAL(3,2),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

### Stored Procedures

#### 1. usp_Update_Jasper_Device
```sql
CREATE PROCEDURE usp_Update_Jasper_Device(
    @iccid VARCHAR(50),
    @rate_plan_id INTEGER,
    @usage_data JSON,
    @device_metadata JSON
)
AS
BEGIN
    BEGIN TRANSACTION;
    
    BEGIN TRY
        -- Update or insert device
        MERGE devices AS target
        USING (VALUES (@iccid, @rate_plan_id, @usage_data, @device_metadata)) 
               AS source (iccid, rate_plan_id, usage_data, device_metadata)
        ON target.iccid = source.iccid
        WHEN MATCHED THEN
            UPDATE SET 
                rate_plan_id = source.rate_plan_id,
                usage_data = source.usage_data,
                device_metadata = source.device_metadata,
                updated_at = CURRENT_TIMESTAMP
        WHEN NOT MATCHED THEN
            INSERT (iccid, rate_plan_id, usage_data, device_metadata, created_at)
            VALUES (source.iccid, source.rate_plan_id, source.usage_data, 
                   source.device_metadata, CURRENT_TIMESTAMP);
        
        -- Update usage statistics
        EXEC usp_Calculate_Usage_Statistics @iccid;
        
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
```

## Algorithm Implementation

### 1. Rate Pool Generation Algorithm

```python
def generate_rate_pool_sequences(communication_groups, rate_plans):
    """
    Generate optimal rate pool sequences for optimization
    """
    rate_pools = []
    
    for group in communication_groups:
        # Calculate usage statistics
        usage_stats = calculate_group_usage_stats(group)
        
        # Generate candidate rate plans
        candidate_plans = filter_candidate_plans(rate_plans, usage_stats)
        
        # Create rate pool with proration settings
        rate_pool = {
            'group_id': group['group_id'],
            'candidate_plans': candidate_plans,
            'max_average_usage': usage_stats['max_average_usage'],
            'proration_factor': calculate_proration_factor(group),
            'optimization_strategies': determine_strategies(group['portal_type'])
        }
        
        rate_pools.append(rate_pool)
    
    return rate_pools
```

### 2. Device Assignment Algorithm

```python
def assign_devices_to_optimal_plans(devices, rate_plans, strategy='grouped_largest_first'):
    """
    Assign devices to optimal rate plans using specified strategy
    """
    if strategy == 'no_grouping_largest_first':
        # Sort devices by usage (largest first)
        sorted_devices = sorted(devices, key=lambda d: d['usage_mb'], reverse=True)
        return assign_individual_devices(sorted_devices, rate_plans)
    
    elif strategy == 'no_grouping_smallest_first':
        # Sort devices by usage (smallest first)
        sorted_devices = sorted(devices, key=lambda d: d['usage_mb'])
        return assign_individual_devices(sorted_devices, rate_plans)
    
    elif strategy == 'grouped_by_plan_largest_first':
        # Group by current plan, then sort by usage
        grouped_devices = group_devices_by_plan(devices)
        return assign_grouped_devices(grouped_devices, rate_plans, reverse=True)
    
    elif strategy == 'grouped_by_plan_smallest_first':
        # Group by current plan, then sort by usage
        grouped_devices = group_devices_by_plan(devices)
        return assign_grouped_devices(grouped_devices, rate_plans, reverse=False)
```

### 3. Cost Optimization Algorithm

```python
def optimize_cost_assignment(devices, rate_plans, constraints=None):
    """
    Optimize device assignments using linear programming approach
    """
    # Define decision variables
    num_devices = len(devices)
    num_plans = len(rate_plans)
    
    # Create assignment matrix (device x plan)
    assignment_matrix = np.zeros((num_devices, num_plans))
    
    # Calculate cost matrix
    cost_matrix = calculate_cost_matrix(devices, rate_plans)
    
    # Apply constraints
    if constraints:
        cost_matrix = apply_constraints(cost_matrix, constraints)
    
    # Solve assignment problem using Hungarian algorithm
    row_indices, col_indices = linear_sum_assignment(cost_matrix)
    
    # Create assignment results
    assignments = []
    for i, device_idx in enumerate(row_indices):
        plan_idx = col_indices[i]
        assignments.append({
            'device_id': devices[device_idx]['id'],
            'iccid': devices[device_idx]['iccid'],
            'assigned_plan_id': rate_plans[plan_idx]['id'],
            'cost': cost_matrix[device_idx][plan_idx],
            'savings': devices[device_idx]['current_cost'] - cost_matrix[device_idx][plan_idx]
        })
    
    return assignments
```

## Error Handling and Recovery

### 1. Retry Logic Implementation

```python
class OptimizationRetryHandler:
    def __init__(self, max_retries=3, base_delay=1, max_delay=60):
        self.max_retries = max_retries
        self.base_delay = base_delay
        self.max_delay = max_delay
    
    def retry_with_backoff(self, func, *args, **kwargs):
        """
        Execute function with exponential backoff retry
        """
        for attempt in range(self.max_retries + 1):
            try:
                return func(*args, **kwargs)
            except Exception as e:
                if attempt == self.max_retries:
                    raise e
                
                # Calculate delay with jitter
                delay = min(self.base_delay * (2 ** attempt), self.max_delay)
                jitter = random.uniform(0, delay * 0.1)
                time.sleep(delay + jitter)
                
                log_warning(f"Retry {attempt + 1}/{self.max_retries} for {func.__name__}: {e}")
```

### 2. Circuit Breaker Pattern

```python
class CircuitBreaker:
    def __init__(self, failure_threshold=5, recovery_timeout=60, expected_exception=Exception):
        self.failure_threshold = failure_threshold
        self.recovery_timeout = recovery_timeout
        self.expected_exception = expected_exception
        self.failure_count = 0
        self.last_failure_time = None
        self.state = 'CLOSED'  # CLOSED, OPEN, HALF_OPEN
    
    def __call__(self, func):
        def wrapper(*args, **kwargs):
            if self.state == 'OPEN':
                if time.time() - self.last_failure_time > self.recovery_timeout:
                    self.state = 'HALF_OPEN'
                else:
                    raise Exception("Circuit breaker is OPEN")
            
            try:
                result = func(*args, **kwargs)
                self.reset()
                return result
            except self.expected_exception as e:
                self.record_failure()
                raise e
        
        return wrapper
    
    def record_failure(self):
        self.failure_count += 1
        self.last_failure_time = time.time()
        if self.failure_count >= self.failure_threshold:
            self.state = 'OPEN'
    
    def reset(self):
        self.failure_count = 0
        self.state = 'CLOSED'
```

### 3. Dead Letter Queue Handling

```python
def handle_dead_letter_queue(dlq_url, max_messages=10):
    """
    Process messages from dead letter queue for manual intervention
    """
    dlq_messages = sqs_client.receive_messages(
        QueueUrl=dlq_url,
        MaxNumberOfMessages=max_messages
    )
    
    for message in dlq_messages:
        try:
            # Parse message content
            message_data = json.loads(message['Body'])
            
            # Log for manual review
            log_error(f"DLQ Message: {message_data}")
            
            # Attempt to identify issue
            issue_type = classify_error(message_data)
            
            # Send alert to operations team
            send_alert({
                'type': 'DLQ_MESSAGE',
                'issue_type': issue_type,
                'message_data': message_data,
                'timestamp': datetime.utcnow().isoformat()
            })
            
            # Move to manual review queue
            move_to_manual_review(message_data)
            
        except Exception as e:
            log_error(f"Error processing DLQ message: {e}")
        finally:
            # Delete message from DLQ
            sqs_client.delete_message(
                QueueUrl=dlq_url,
                ReceiptHandle=message['ReceiptHandle']
            )
```

## Performance Optimization

### 1. Redis Caching Strategy

```python
class OptimizationCache:
    def __init__(self, redis_client, ttl=3600):
        self.redis = redis_client
        self.ttl = ttl
    
    def cache_device_data(self, session_id, devices):
        """
        Cache device data for optimization session
        """
        key = f"optimization:devices:{session_id}"
        serialized_data = json.dumps(devices)
        
        # Use compression for large datasets
        if len(serialized_data) > 1024:
            compressed_data = gzip.compress(serialized_data.encode())
            self.redis.set(key, compressed_data, ex=self.ttl)
            self.redis.set(f"{key}:compressed", "true", ex=self.ttl)
        else:
            self.redis.set(key, serialized_data, ex=self.ttl)
    
    def get_cached_devices(self, session_id):
        """
        Retrieve cached device data
        """
        key = f"optimization:devices:{session_id}"
        cached_data = self.redis.get(key)
        
        if not cached_data:
            return None
        
        # Check if data is compressed
        if self.redis.get(f"{key}:compressed"):
            decompressed_data = gzip.decompress(cached_data).decode()
            return json.loads(decompressed_data)
        else:
            return json.loads(cached_data)
```

### 2. Database Connection Pooling

```python
class DatabasePool:
    def __init__(self, connection_string, pool_size=10, max_overflow=20):
        self.engine = create_engine(
            connection_string,
            pool_size=pool_size,
            max_overflow=max_overflow,
            pool_pre_ping=True,
            pool_recycle=3600
        )
        self.Session = sessionmaker(bind=self.engine)
    
    def get_session(self):
        """
        Get database session with automatic retry
        """
        max_retries = 3
        for attempt in range(max_retries):
            try:
                session = self.Session()
                # Test connection
                session.execute("SELECT 1")
                return session
            except Exception as e:
                if attempt == max_retries - 1:
                    raise e
                time.sleep(1)
    
    def execute_with_retry(self, query, params=None):
        """
        Execute query with automatic retry on transient failures
        """
        max_retries = 3
        for attempt in range(max_retries):
            session = None
            try:
                session = self.get_session()
                result = session.execute(query, params or {})
                session.commit()
                return result.fetchall()
            except Exception as e:
                if session:
                    session.rollback()
                
                if attempt == max_retries - 1:
                    raise e
                
                time.sleep(2 ** attempt)
            finally:
                if session:
                    session.close()
```

## Monitoring and Alerting

### 1. CloudWatch Metrics

```python
def publish_optimization_metrics(session_id, metrics):
    """
    Publish optimization metrics to CloudWatch
    """
    cloudwatch = boto3.client('cloudwatch')
    
    metric_data = [
        {
            'MetricName': 'OptimizationDuration',
            'Value': metrics['duration_seconds'],
            'Unit': 'Seconds',
            'Dimensions': [
                {'Name': 'SessionId', 'Value': session_id}
            ]
        },
        {
            'MetricName': 'DevicesOptimized',
            'Value': metrics['device_count'],
            'Unit': 'Count',
            'Dimensions': [
                {'Name': 'SessionId', 'Value': session_id}
            ]
        },
        {
            'MetricName': 'CostSavings',
            'Value': metrics['cost_savings'],
            'Unit': 'None',
            'Dimensions': [
                {'Name': 'SessionId', 'Value': session_id}
            ]
        },
        {
            'MetricName': 'SavingsPercentage',
            'Value': metrics['savings_percentage'],
            'Unit': 'Percent',
            'Dimensions': [
                {'Name': 'SessionId', 'Value': session_id}
            ]
        }
    ]
    
    cloudwatch.put_metric_data(
        Namespace='CarrierOptimization',
        MetricData=metric_data
    )
```

### 2. Alert Configuration

```python
def setup_optimization_alerts():
    """
    Set up CloudWatch alarms for optimization process
    """
    cloudwatch = boto3.client('cloudwatch')
    
    # High failure rate alarm
    cloudwatch.put_metric_alarm(
        AlarmName='CarrierOptimization-HighFailureRate',
        ComparisonOperator='GreaterThanThreshold',
        EvaluationPeriods=2,
        MetricName='Errors',
        Namespace='AWS/Lambda',
        Period=300,
        Statistic='Sum',
        Threshold=5.0,
        ActionsEnabled=True,
        AlarmActions=[
            'arn:aws:sns:region:account:optimization-alerts'
        ],
        AlarmDescription='High failure rate in optimization process',
        Dimensions=[
            {
                'Name': 'FunctionName',
                'Value': 'AltaworxSimCardCostOptimizer'
            }
        ]
    )
    
    # Long execution time alarm
    cloudwatch.put_metric_alarm(
        AlarmName='CarrierOptimization-LongExecutionTime',
        ComparisonOperator='GreaterThanThreshold',
        EvaluationPeriods=1,
        MetricName='Duration',
        Namespace='AWS/Lambda',
        Period=300,
        Statistic='Average',
        Threshold=600000.0,  # 10 minutes in milliseconds
        ActionsEnabled=True,
        AlarmActions=[
            'arn:aws:sns:region:account:optimization-alerts'
        ],
        AlarmDescription='Optimization process taking too long',
        Dimensions=[
            {
                'Name': 'FunctionName',
                'Value': 'AltaworxSimCardCostOptimizer'
            }
        ]
    )
```

## Configuration Management

### 1. Environment Configuration

```python
class OptimizationConfig:
    def __init__(self):
        self.load_config()
    
    def load_config(self):
        """
        Load configuration from environment variables and parameter store
        """
        self.config = {
            # SQS Configuration
            'carrier_optimization_queue_url': os.getenv('CARRIER_OPTIMIZATION_QUEUE_URL'),
            'device_sync_queue_url': os.getenv('DEVICE_SYNC_QUEUE_URL'),
            'queues_per_instance': int(os.getenv('QUEUES_PER_INSTANCE', '10')),
            
            # Processing Configuration
            'max_pages_to_process': int(os.getenv('MAX_PAGES_TO_PROCESS', '1000')),
            'sanity_check_time_limit': int(os.getenv('SANITY_CHECK_TIME_LIMIT', '300')),
            'batch_size': int(os.getenv('BATCH_SIZE', '100')),
            
            # Database Configuration
            'db_connection_string': self.get_parameter('/optimization/db/connection_string'),
            'db_timeout': int(os.getenv('DB_TIMEOUT', '30')),
            'db_pool_size': int(os.getenv('DB_POOL_SIZE', '10')),
            
            # Redis Configuration
            'redis_host': os.getenv('REDIS_HOST'),
            'redis_port': int(os.getenv('REDIS_PORT', '6379')),
            'redis_ttl': int(os.getenv('REDIS_TTL', '3600')),
            
            # API Configuration
            'jasper_api_url': self.get_parameter('/optimization/jasper/api_url'),
            'jasper_api_key': self.get_parameter('/optimization/jasper/api_key'),
            'jasper_timeout': int(os.getenv('JASPER_TIMEOUT', '60')),
            
            # Notification Configuration
            'sns_topic_arn': os.getenv('SNS_TOPIC_ARN'),
            'email_from': os.getenv('EMAIL_FROM'),
            'email_recipients': os.getenv('EMAIL_RECIPIENTS', '').split(','),
            
            # Optimization Configuration
            'enable_cross_provider': os.getenv('ENABLE_CROSS_PROVIDER', 'false').lower() == 'true',
            'enable_rate_plan_updates': os.getenv('ENABLE_RATE_PLAN_UPDATES', 'true').lower() == 'true',
            'update_buffer_minutes': int(os.getenv('UPDATE_BUFFER_MINUTES', '10')),
            'max_update_batch_size': int(os.getenv('MAX_UPDATE_BATCH_SIZE', '250'))
        }
    
    def get_parameter(self, parameter_name):
        """
        Get parameter from AWS Parameter Store
        """
        ssm = boto3.client('ssm')
        try:
            response = ssm.get_parameter(
                Name=parameter_name,
                WithDecryption=True
            )
            return response['Parameter']['Value']
        except Exception as e:
            log_error(f"Error getting parameter {parameter_name}: {e}")
            return None
```

## Troubleshooting Guide

### Common Issues and Solutions

#### 1. Device Sync Failures
```
Issue: Jasper API timeouts during device sync
Symptoms: 
- High error rates in AltaworxJasperAWSGetDevicesQueue
- Incomplete device data in staging tables
- Stuck optimization sessions

Solution:
1. Check API endpoint health
2. Increase timeout values
3. Implement circuit breaker
4. Reduce batch sizes
5. Enable retry logic with backoff
```

#### 2. Memory Issues in Optimization
```
Issue: Lambda functions running out of memory
Symptoms:
- Lambda timeouts during optimization
- Out of memory errors in logs
- Incomplete optimization results

Solution:
1. Increase Lambda memory allocation
2. Enable Redis caching for large datasets
3. Implement pagination for large device sets
4. Use Lambda chaining for memory-intensive operations
```

#### 3. Database Connection Issues
```
Issue: Database connection pool exhaustion
Symptoms:
- Connection timeout errors
- Failed database operations
- Hanging Lambda functions

Solution:
1. Increase connection pool size
2. Implement connection retry logic
3. Use connection pooling with proper cleanup
4. Monitor database connection metrics
```

#### 4. Queue Processing Delays
```
Issue: SQS message processing delays
Symptoms:
- Long optimization times
- Messages stuck in queues
- Timeout errors

Solution:
1. Increase Lambda concurrency limits
2. Optimize message batch sizes
3. Implement dead letter queues
4. Monitor queue depths and processing rates
```

### Diagnostic Commands

```bash
# Check Lambda function logs
aws logs filter-log-events \
  --log-group-name /aws/lambda/AltaworxSimCardCostOptimizer \
  --start-time $(date -d '1 hour ago' +%s)000 \
  --filter-pattern "ERROR"

# Monitor SQS queue depths
aws sqs get-queue-attributes \
  --queue-url $CARRIER_OPTIMIZATION_QUEUE_URL \
  --attribute-names ApproximateNumberOfMessages

# Check optimization session status
aws rds-data execute-statement \
  --resource-arn $DB_CLUSTER_ARN \
  --secret-arn $DB_SECRET_ARN \
  --database optimization \
  --sql "SELECT session_id, status, device_count, cost_savings FROM optimization_sessions WHERE created_at > NOW() - INTERVAL 1 DAY"
```

This comprehensive documentation provides detailed technical insights into the Carrier Optimization process, covering all aspects from system architecture to troubleshooting procedures. The documentation serves as a complete reference for understanding, implementing, and maintaining the optimization pipeline.