# Operations Guide

## System Architecture Overview

### AWS Infrastructure

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Altaworx SIM Card Cost Optimization                │
│                              AWS Infrastructure                             │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   CloudWatch    │    │   Lambda        │    │   SQS Queues    │
│   Events        │───▶│   Scheduler     │───▶│   Optimization  │
│   (Cron Jobs)   │    │   (Queue Creator)│    │   Triggers      │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                                        │
                                                        ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Jasper API    │◀───│   Device Sync   │◀───│   Lambda        │
│   (External)    │    │   Lambda        │    │   Queue Processor│
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                                        │
                                                        ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Redis Cache   │◀───│   Core          │◀───│   Multiple      │
│   (ElastiCache) │    │   Optimizer     │    │   Optimizer     │
└─────────────────┘    │   Lambda        │    │   Lambda        │
                       └─────────────────┘    │   Instances     │
                                              └─────────────────┘
                                                        │
                                                        ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   SQL Server    │◀───│   Cleanup       │◀───│   Results       │
│   Database      │    │   Lambda        │    │   Processing    │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                 │
                                 ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   SES Email     │◀───│   Email         │    │   AMOP 2.0      │
│   Service       │    │   Notifications │    │   API           │
└─────────────────┘    └─────────────────┘    └─────────────────┘
```

## Deployment Procedures

### 1. Prerequisites

#### AWS Account Setup
- **IAM Roles**: Lambda execution roles with appropriate permissions
- **VPC Configuration**: Network setup for database access
- **Security Groups**: Inbound/outbound rules for Lambda functions
- **KMS Keys**: Encryption keys for sensitive data

#### Database Setup
- **SQL Server Instance**: RDS or EC2-based SQL Server
- **Database Schema**: Create optimization tables and stored procedures
- **Connection Strings**: Configure encrypted connection strings
- **User Permissions**: Create database users with appropriate access

#### External Services
- **Jasper API**: Obtain API credentials and endpoints
- **AMOP 2.0**: Configure API integration settings
- **Redis Cache**: Set up ElastiCache cluster (optional)

### 2. Lambda Function Deployment

#### Build and Package
```bash
# Build the C# project
dotnet build --configuration Release

# Create deployment package
dotnet lambda package --configuration Release --framework net6.0 --output-package optimization-package.zip
```

#### Deploy Functions
```bash
# Deploy core optimizer
aws lambda create-function \
  --function-name altaworx-sim-card-optimizer \
  --runtime dotnet6 \
  --role arn:aws:iam::account:role/lambda-execution-role \
  --handler AltaworxSimCardCostOptimizer::AltaworxSimCardCostOptimizer.Function::Handler \
  --zip-file fileb://optimization-package.zip \
  --timeout 900 \
  --memory-size 1024

# Deploy device sync function
aws lambda create-function \
  --function-name altaworx-jasper-device-sync \
  --runtime dotnet6 \
  --role arn:aws:iam::account:role/lambda-execution-role \
  --handler AltaworxJasperAWSGetDevicesQueue::AltaworxJasperAWSGetDevicesQueue.Function::FunctionHandler \
  --zip-file fileb://optimization-package.zip \
  --timeout 300 \
  --memory-size 512

# Deploy cleanup function
aws lambda create-function \
  --function-name altaworx-optimization-cleanup \
  --runtime dotnet6 \
  --role arn:aws:iam::account:role/lambda-execution-role \
  --handler AltaworxSimCardCostOptimizerCleanup::AltaworxSimCardCostOptimizerCleanup.Function::Handler \
  --zip-file fileb://optimization-package.zip \
  --timeout 900 \
  --memory-size 256

# Deploy queue creator function
aws lambda create-function \
  --function-name altaworx-queue-creator \
  --runtime dotnet6 \
  --role arn:aws:iam::account:role/lambda-execution-role \
  --handler QueueCarrierPlanOptimization::QueueCarrierPlanOptimization.Function::Handler \
  --zip-file fileb://optimization-package.zip \
  --timeout 300 \
  --memory-size 256
```

### 3. Environment Configuration

#### Lambda Environment Variables
```json
{
  "Variables": {
    "WatchQueueURL": "https://sqs.us-east-1.amazonaws.com/account/optimization-watch-queue",
    "DeviceSyncQueueURL": "https://sqs.us-east-1.amazonaws.com/account/device-sync-queue",
    "CarrierOptimizationQueueURL": "https://sqs.us-east-1.amazonaws.com/account/carrier-optimization-queue",
    "OptimizationUsageQueueURL": "https://sqs.us-east-1.amazonaws.com/account/optimization-usage-queue",
    "ExportDeviceUsageQueueURL": "https://sqs.us-east-1.amazonaws.com/account/export-device-usage-queue",
    "RatePlanUpdateQueueURL": "https://sqs.us-east-1.amazonaws.com/account/rate-plan-update-queue",
    "ProxyUrl": "https://amop-api.altaworx.com/api/v2/optimization",
    "JasperDevicesGetPath": "devices",
    "QueuesPerInstance": "5",
    "MaxPagesToProcess": "100",
    "SanityCheckTimeLimit": "180",
    "OptCustomerCleanUpDelaySeconds": "300",
    "CleanUpSendEmailRetryCount": "3",
    "ErrorNotificationEmailReceiver": "operations@altaworx.com",
    "AWSEnv": "production"
  }
}
```

#### SQS Queue Configuration
```bash
# Create optimization queues
aws sqs create-queue --queue-name optimization-watch-queue \
  --attributes VisibilityTimeoutSeconds=900,MessageRetentionPeriod=1209600

aws sqs create-queue --queue-name device-sync-queue \
  --attributes VisibilityTimeoutSeconds=300,MessageRetentionPeriod=1209600

aws sqs create-queue --queue-name carrier-optimization-queue \
  --attributes VisibilityTimeoutSeconds=300,MessageRetentionPeriod=1209600

aws sqs create-queue --queue-name optimization-usage-queue \
  --attributes VisibilityTimeoutSeconds=300,MessageRetentionPeriod=1209600

aws sqs create-queue --queue-name export-device-usage-queue \
  --attributes VisibilityTimeoutSeconds=300,MessageRetentionPeriod=1209600

aws sqs create-queue --queue-name rate-plan-update-queue \
  --attributes VisibilityTimeoutSeconds=900,MessageRetentionPeriod=1209600
```

### 4. Monitoring Setup

#### CloudWatch Alarms
```bash
# High error rate alarm
aws cloudwatch put-metric-alarm \
  --alarm-name "Altaworx-High-Error-Rate" \
  --alarm-description "Alarm when error rate exceeds 5%" \
  --metric-name ErrorRate \
  --namespace AWS/Lambda \
  --statistic Average \
  --period 300 \
  --threshold 5.0 \
  --comparison-operator GreaterThanThreshold \
  --evaluation-periods 2

# Long processing time alarm
aws cloudwatch put-metric-alarm \
  --alarm-name "Altaworx-Long-Processing-Time" \
  --alarm-description "Alarm when processing time exceeds 10 minutes" \
  --metric-name Duration \
  --namespace AWS/Lambda \
  --statistic Average \
  --period 300 \
  --threshold 600000 \
  --comparison-operator GreaterThanThreshold \
  --evaluation-periods 1

# Queue depth alarm
aws cloudwatch put-metric-alarm \
  --alarm-name "Altaworx-High-Queue-Depth" \
  --alarm-description "Alarm when queue depth exceeds 100 messages" \
  --metric-name ApproximateNumberOfMessages \
  --namespace AWS/SQS \
  --statistic Average \
  --period 300 \
  --threshold 100 \
  --comparison-operator GreaterThanThreshold \
  --evaluation-periods 2
```

## Monitoring and Alerting

### 1. Key Performance Indicators (KPIs)

#### Optimization Metrics
| Metric | Target | Critical Threshold |
|--------|--------|-------------------|
| Cost Savings Percentage | > 10% | < 5% |
| Optimization Success Rate | > 95% | < 90% |
| Processing Time | < 5 minutes | > 10 minutes |
| Device Count Accuracy | > 95% | < 90% |

#### System Health Metrics
| Metric | Target | Critical Threshold |
|--------|--------|-------------------|
| Lambda Error Rate | < 1% | > 5% |
| Database Connection Success | > 99% | < 95% |
| API Response Time | < 2 seconds | > 5 seconds |
| Cache Hit Rate | > 80% | < 50% |

### 2. Dashboard Configuration

#### CloudWatch Dashboard
```json
{
  "widgets": [
    {
      "type": "metric",
      "properties": {
        "metrics": [
          ["AWS/Lambda", "Duration", "FunctionName", "altaworx-sim-card-optimizer"],
          ["AWS/Lambda", "Errors", "FunctionName", "altaworx-sim-card-optimizer"],
          ["AWS/Lambda", "Invocations", "FunctionName", "altaworx-sim-card-optimizer"]
        ],
        "period": 300,
        "stat": "Average",
        "region": "us-east-1",
        "title": "Lambda Performance Metrics"
      }
    },
    {
      "type": "metric",
      "properties": {
        "metrics": [
          ["AWS/SQS", "ApproximateNumberOfMessages", "QueueName", "optimization-watch-queue"],
          ["AWS/SQS", "NumberOfMessagesSent", "QueueName", "optimization-watch-queue"],
          ["AWS/SQS", "NumberOfMessagesReceived", "QueueName", "optimization-watch-queue"]
        ],
        "period": 300,
        "stat": "Average",
        "region": "us-east-1",
        "title": "SQS Queue Metrics"
      }
    }
  ]
}
```

### 3. Log Analysis

#### Key Log Patterns
```bash
# Monitor optimization success/failure
aws logs filter-log-events \
  --log-group-name "/aws/lambda/altaworx-sim-card-optimizer" \
  --filter-pattern "OptimizationStatus.CompleteWithSuccess" \
  --start-time $(date -d '1 hour ago' +%s)000

# Monitor error patterns
aws logs filter-log-events \
  --log-group-name "/aws/lambda/altaworx-sim-card-optimizer" \
  --filter-pattern "ERROR" \
  --start-time $(date -d '1 hour ago' +%s)000

# Monitor API timeout issues
aws logs filter-log-events \
  --log-group-name "/aws/lambda/altaworx-jasper-device-sync" \
  --filter-pattern "TimeoutException" \
  --start-time $(date -d '1 hour ago' +%s)000
```

## Maintenance Procedures

### 1. Regular Maintenance Tasks

#### Daily Tasks
- **Monitor Dashboard**: Check system health metrics
- **Review Error Logs**: Identify and address any critical errors
- **Queue Depth Check**: Ensure queues are processing normally
- **Cost Savings Validation**: Verify optimization effectiveness

#### Weekly Tasks
- **Database Cleanup**: Remove old optimization data
- **Cache Performance Review**: Analyze cache hit rates
- **API Performance Analysis**: Review external API response times
- **Billing Period Validation**: Ensure correct billing period configurations

#### Monthly Tasks
- **Performance Optimization**: Analyze and optimize slow-running processes
- **Security Review**: Review IAM policies and access logs
- **Capacity Planning**: Assess resource usage and scaling needs
- **Documentation Update**: Update operational procedures and configurations

### 2. Database Maintenance

#### Cleanup Old Data
```sql
-- Delete optimization instances older than 90 days
DELETE FROM OptimizationInstance 
WHERE RunStartTime < DATEADD(DAY, -90, GETUTCDATE()) 
AND RunStatusId IN (2, 3); -- CompleteWithSuccess, CompleteWithErrors

-- Delete old device results
DELETE FROM OptimizationM2MDeviceResult 
WHERE QueueId IN (
    SELECT q.Id FROM OptimizationQueue q
    JOIN OptimizationInstance i ON q.InstanceId = i.Id
    WHERE i.RunStartTime < DATEADD(DAY, -90, GETUTCDATE())
);

-- Delete old mobility results
DELETE FROM OptimizationMobilityDeviceResult 
WHERE QueueId IN (
    SELECT q.Id FROM OptimizationQueue q
    JOIN OptimizationInstance i ON q.InstanceId = i.Id
    WHERE i.RunStartTime < DATEADD(DAY, -90, GETUTCDATE())
);

-- Update statistics
UPDATE STATISTICS OptimizationInstance;
UPDATE STATISTICS OptimizationQueue;
UPDATE STATISTICS OptimizationM2MDeviceResult;
UPDATE STATISTICS OptimizationMobilityDeviceResult;
```

#### Index Maintenance
```sql
-- Rebuild fragmented indexes
EXEC sp_MSforeachtable 'ALTER INDEX ALL ON ? REBUILD WITH (FILLFACTOR = 90)';

-- Update statistics on key tables
UPDATE STATISTICS OptimizationInstance WITH FULLSCAN;
UPDATE STATISTICS OptimizationQueue WITH FULLSCAN;
UPDATE STATISTICS ServiceProvider WITH FULLSCAN;
UPDATE STATISTICS CarrierRatePlan WITH FULLSCAN;
```

### 3. Cache Maintenance

#### Redis Cache Cleanup
```bash
# Connect to Redis cluster
redis-cli -h elasticache-cluster-endpoint -p 6379

# Clear expired keys
redis-cli --scan --pattern "*" | xargs redis-cli del

# Check cache statistics
redis-cli info memory
redis-cli info stats
```

#### Cache Performance Analysis
```bash
# Monitor cache hit ratio
redis-cli info stats | grep keyspace_hits
redis-cli info stats | grep keyspace_misses

# Check memory usage
redis-cli info memory | grep used_memory_human
redis-cli info memory | grep maxmemory_human
```

## Troubleshooting Procedures

### 1. Common Issues and Solutions

#### High Error Rate
**Symptoms**: CloudWatch alarms showing high error rate
**Investigation Steps**:
1. Check Lambda function logs for error patterns
2. Review SQS dead letter queues
3. Verify database connectivity
4. Check external API status

**Resolution**:
```bash
# Check Lambda function errors
aws logs filter-log-events \
  --log-group-name "/aws/lambda/altaworx-sim-card-optimizer" \
  --filter-pattern "ERROR" \
  --start-time $(date -d '1 hour ago' +%s)000

# Check SQS dead letter queue
aws sqs receive-message \
  --queue-url https://sqs.us-east-1.amazonaws.com/account/optimization-watch-queue-dlq \
  --max-number-of-messages 10

# Test database connectivity
sqlcmd -S server-name -d database-name -U username -P password -Q "SELECT 1"
```

#### Processing Timeouts
**Symptoms**: Lambda functions timing out consistently
**Investigation Steps**:
1. Review function execution duration
2. Check database query performance
3. Analyze optimization algorithm efficiency
4. Verify cache performance

**Resolution**:
```bash
# Increase Lambda timeout
aws lambda update-function-configuration \
  --function-name altaworx-sim-card-optimizer \
  --timeout 900

# Increase memory allocation
aws lambda update-function-configuration \
  --function-name altaworx-sim-card-optimizer \
  --memory-size 1536

# Enable provisioned concurrency
aws lambda put-provisioned-concurrency-config \
  --function-name altaworx-sim-card-optimizer \
  --provisioned-concurrency-count 10
```

#### Cache Connection Failures
**Symptoms**: "Redis cache is configured but not reachable" errors
**Investigation Steps**:
1. Check ElastiCache cluster status
2. Verify security group configurations
3. Test network connectivity
4. Review cache connection strings

**Resolution**:
```bash
# Check ElastiCache cluster status
aws elasticache describe-cache-clusters \
  --cache-cluster-id altaworx-optimization-cache

# Test connectivity from Lambda subnet
aws ec2 describe-security-groups \
  --group-ids sg-lambda-security-group-id

# Update cache configuration
aws elasticache modify-cache-cluster \
  --cache-cluster-id altaworx-optimization-cache \
  --apply-immediately
```

### 2. Emergency Procedures

#### System Outage Response
1. **Immediate Actions**:
   - Disable CloudWatch Events triggers
   - Stop all running Lambda functions
   - Notify stakeholders of the outage

2. **Investigation**:
   - Review CloudWatch logs and metrics
   - Check AWS service health dashboard
   - Verify database availability
   - Test external API connectivity

3. **Resolution**:
   - Fix identified issues
   - Gradually re-enable services
   - Monitor system stability
   - Document lessons learned

#### Data Corruption Recovery
1. **Immediate Actions**:
   - Stop all optimization processes
   - Backup current database state
   - Identify corruption scope

2. **Recovery Steps**:
   - Restore from latest clean backup
   - Replay transactions if possible
   - Validate data integrity
   - Resume operations carefully

## Performance Tuning

### 1. Lambda Function Optimization

#### Memory and Timeout Tuning
```bash
# Analyze function performance
aws logs filter-log-events \
  --log-group-name "/aws/lambda/altaworx-sim-card-optimizer" \
  --filter-pattern "REPORT" \
  --start-time $(date -d '1 day ago' +%s)000

# Update function configuration based on analysis
aws lambda update-function-configuration \
  --function-name altaworx-sim-card-optimizer \
  --memory-size 1536 \
  --timeout 600
```

#### Concurrency Configuration
```bash
# Set reserved concurrency
aws lambda put-reserved-concurrency-config \
  --function-name altaworx-sim-card-optimizer \
  --reserved-concurrency-count 50

# Configure provisioned concurrency
aws lambda put-provisioned-concurrency-config \
  --function-name altaworx-sim-card-optimizer \
  --provisioned-concurrency-count 5
```

### 2. Database Performance Optimization

#### Query Optimization
```sql
-- Add missing indexes
CREATE INDEX IX_OptimizationInstance_RunStartTime 
ON OptimizationInstance (RunStartTime) 
INCLUDE (Id, RunStatusId, ServiceProviderId);

CREATE INDEX IX_OptimizationQueue_CommPlanGroupId 
ON OptimizationQueue (CommPlanGroupId) 
INCLUDE (Id, TotalCost, RunEndTime);

CREATE INDEX IX_OptimizationSimCard_InstanceId 
ON OptimizationSimCard (InstanceId) 
INCLUDE (Id, ICCID, CycleDataUsageMB);
```

#### Connection Pooling
```csharp
// Optimize connection string
var connectionString = "Server=server-name;Database=database-name;Integrated Security=true;Connection Timeout=30;Command Timeout=900;Max Pool Size=100;Min Pool Size=5;Pooling=true;";
```

### 3. Caching Strategy Optimization

#### Redis Configuration
```bash
# Optimize Redis configuration
redis-cli config set maxmemory-policy allkeys-lru
redis-cli config set maxmemory 2gb
redis-cli config set timeout 300
redis-cli config set tcp-keepalive 60
```

#### Cache Key Optimization
```csharp
// Implement cache key expiration
var cacheKey = $"simcards:{instanceId}:{commPlanGroupId}";
var expiration = TimeSpan.FromHours(2);
cache.Set(cacheKey, simCards, expiration);
```

## Security Procedures

### 1. Access Control

#### IAM Role Management
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "logs:CreateLogGroup",
        "logs:CreateLogStream",
        "logs:PutLogEvents"
      ],
      "Resource": "arn:aws:logs:*:*:*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "sqs:ReceiveMessage",
        "sqs:DeleteMessage",
        "sqs:SendMessage",
        "sqs:GetQueueAttributes"
      ],
      "Resource": "arn:aws:sqs:*:*:altaworx-optimization-*"
    }
  ]
}
```

#### Database Security
```sql
-- Create restricted database user
CREATE LOGIN optimization_lambda WITH PASSWORD = 'SecurePassword123!';
CREATE USER optimization_lambda FOR LOGIN optimization_lambda;

-- Grant minimal required permissions
GRANT EXECUTE ON SCHEMA::dbo TO optimization_lambda;
GRANT SELECT, INSERT, UPDATE, DELETE ON OptimizationInstance TO optimization_lambda;
GRANT SELECT, INSERT, UPDATE, DELETE ON OptimizationQueue TO optimization_lambda;
GRANT SELECT, INSERT, UPDATE, DELETE ON OptimizationSimCard TO optimization_lambda;
```

### 2. Encryption

#### Data at Rest
- **Database**: Enable Transparent Data Encryption (TDE)
- **S3 Buckets**: Enable default encryption
- **EBS Volumes**: Enable encryption

#### Data in Transit
- **Database Connections**: Use SSL/TLS
- **API Calls**: HTTPS only
- **Internal Communication**: VPC endpoints

### 3. Audit and Compliance

#### CloudTrail Configuration
```bash
# Enable CloudTrail logging
aws cloudtrail create-trail \
  --name altaworx-optimization-trail \
  --s3-bucket-name altaworx-audit-logs \
  --include-global-service-events \
  --is-multi-region-trail \
  --enable-log-file-validation
```

#### Log Retention
```bash
# Set log retention policies
aws logs put-retention-policy \
  --log-group-name "/aws/lambda/altaworx-sim-card-optimizer" \
  --retention-in-days 30

aws logs put-retention-policy \
  --log-group-name "/aws/lambda/altaworx-jasper-device-sync" \
  --retention-in-days 30
```

## Disaster Recovery

### 1. Backup Procedures

#### Database Backups
```sql
-- Create full database backup
BACKUP DATABASE OptimizationDB 
TO DISK = 'C:\Backups\OptimizationDB_Full.bak'
WITH COMPRESSION, CHECKSUM;

-- Create differential backup
BACKUP DATABASE OptimizationDB 
TO DISK = 'C:\Backups\OptimizationDB_Diff.bak'
WITH DIFFERENTIAL, COMPRESSION, CHECKSUM;

-- Create transaction log backup
BACKUP LOG OptimizationDB 
TO DISK = 'C:\Backups\OptimizationDB_Log.trn'
WITH COMPRESSION, CHECKSUM;
```

#### Configuration Backups
```bash
# Backup Lambda function configurations
aws lambda list-functions --query 'Functions[?starts_with(FunctionName, `altaworx`)]' > lambda-functions-backup.json

# Backup SQS queue configurations
aws sqs list-queues --queue-name-prefix altaworx > sqs-queues-backup.json

# Backup CloudWatch alarms
aws cloudwatch describe-alarms --alarm-names "Altaworx-High-Error-Rate" > cloudwatch-alarms-backup.json
```

### 2. Recovery Procedures

#### Database Recovery
```sql
-- Restore from full backup
RESTORE DATABASE OptimizationDB 
FROM DISK = 'C:\Backups\OptimizationDB_Full.bak'
WITH REPLACE, CHECKDB;

-- Restore from differential backup
RESTORE DATABASE OptimizationDB 
FROM DISK = 'C:\Backups\OptimizationDB_Diff.bak'
WITH NORECOVERY, CHECKDB;

-- Restore transaction log
RESTORE LOG OptimizationDB 
FROM DISK = 'C:\Backups\OptimizationDB_Log.trn'
WITH RECOVERY, CHECKDB;
```

#### Service Recovery
```bash
# Redeploy Lambda functions
aws lambda update-function-code \
  --function-name altaworx-sim-card-optimizer \
  --zip-file fileb://optimization-package.zip

# Recreate SQS queues if needed
aws sqs create-queue --queue-name optimization-watch-queue \
  --attributes file://queue-attributes.json

# Restore CloudWatch alarms
aws cloudwatch put-metric-alarm --cli-input-json file://cloudwatch-alarms-backup.json
```

This operations guide provides comprehensive procedures for deploying, monitoring, maintaining, and troubleshooting the Altaworx SIM Card Cost Optimization system.