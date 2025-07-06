using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Altaworx.SimCard.Cost.Optimizer.Core;
using Altaworx.SimCard.Cost.Optimizer.Core.Enumerations;
using Altaworx.SimCard.Cost.Optimizer.Core.Helpers;
using Altaworx.SimCard.Cost.Optimizer.Core.Models;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amop.Core.Logger;
using MimeKit;
using Amop.Core.Models;
using Amop.Core.Models.Integration;
using Amop.Core.Repositories.Integration;
using System.Text;
using Amop.Core.Models.Optimization;
using System.Net.Http;
using System.Dynamic;
using Altaworx.AWS.Core.Helpers;
using Newtonsoft.Json;
// to use OptimizationConstant
using Amop.Core.Constants;
// to use LogTypeConstant
using Altaworx.AWS.Core.Helpers.Constants;
using SQLConstant = Amop.Core.Constants.SQLConstant;
using Amop.Core.Helpers;
using Altaworx.SimCard.Cost.Optimizer.Core.Factories;
using Altaworx.SimCard.Cost.Optimizer.Core.Repositories.CarrierRatePlan;
// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace Altaworx.SimCard.Cost.Optimizer.Cleanup
{
    public class Function : AwsFunctionBase
    {
        private string _watchQueueUrl = Environment.GetEnvironmentVariable("WatchQueueURL");
        private string _proxyUrl = Environment.GetEnvironmentVariable("ProxyUrl");
        private int _optCustomerCleanUpDelaySeconds = Convert.ToInt32(Environment.GetEnvironmentVariable("OptCustomerCleanUpDelaySeconds"));
        private int _cleanUpSendEmailRetryCount = Convert.ToInt32(Environment.GetEnvironmentVariable("CleanUpSendEmailRetryCount"));
        /// <summary>
        /// A simple function that takes a string and does a ToUpper
        /// </summary>
        /// <param name="sqsEvent"></param>
        /// <param name="context"></param>
        /// <returns></returns>

        //statuses that indicate the queue have been processed by another Optimizer instance (SQS 'at-least-once' delivery)
        private static readonly List<OptimizationStatus> INSTANCE_FINISHED_STATUSES = new List<OptimizationStatus>(){
            OptimizationStatus.CleaningUp,
            OptimizationStatus.CompleteWithSuccess,
            OptimizationStatus.CompleteWithErrors
        };

        public void Handler(SQSEvent sqsEvent, ILambdaContext context)
        {
            KeySysLambdaContext keysysContext = null;
            try
            {
                keysysContext = BaseFunctionHandler(context);

                if (string.IsNullOrEmpty(_watchQueueUrl))
                {
                    _watchQueueUrl = context.ClientContext.Environment["WatchQueueURL"];
                }
                if (string.IsNullOrEmpty(_proxyUrl))
                {
                    _proxyUrl = context.ClientContext.Environment["ProxyUrl"];
                }
                if (_optCustomerCleanUpDelaySeconds == 0)
                {
                    _optCustomerCleanUpDelaySeconds = Convert.ToInt32(context.ClientContext.Environment["OptCustomerCleanUpDelaySeconds"]);
                }
                if (_cleanUpSendEmailRetryCount == 0)
                {
                    _cleanUpSendEmailRetryCount = Convert.ToInt32(context.ClientContext.Environment["CleanUpSendEmailRetryCount"]);
                }
                InitializeRepositories(context, keysysContext);

                ProcessEvent(keysysContext, sqsEvent);
            }
            catch (Exception ex)
            {
                LogInfo(keysysContext, "EXCEPTION", ex.Message);
            }

            CleanUp(keysysContext);
        }

        private void ProcessEvent(KeySysLambdaContext context, SQSEvent sqsEvent)
        {
            LogInfo(context, "SUB", "ProcessEvent");
            if (sqsEvent.Records.Count > 0)
            {
                if (sqsEvent.Records.Count == 1)
                {
                    ProcessEventRecord(context, sqsEvent.Records[0]);
                }
                else
                {
                    LogInfo(context, "EXCEPTION", $"Expected a single message, received {sqsEvent.Records.Count}");
                }
            }
        }

        private void ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
        {
            LogInfo(context, "SUB", "ProcessEventRecord");
            if (message.MessageAttributes.ContainsKey("InstanceId"))
            {
                var instanceIdString = message.MessageAttributes["InstanceId"].StringValue;
                var instanceId = long.Parse(instanceIdString);

                var retryCount = 0;
                if (message.MessageAttributes.ContainsKey("RetryCount"))
                {
                    var retryCountString = message.MessageAttributes["RetryCount"].StringValue;
                    retryCount = int.Parse(retryCountString);
                }

                bool isCustomerOptimization = false;
                if (message.MessageAttributes.ContainsKey("IsCustomerOptimization"))
                {
                    isCustomerOptimization = Convert.ToBoolean(message.MessageAttributes["IsCustomerOptimization"].StringValue);
                }

                bool isLastInstance = false;
                if (message.MessageAttributes.ContainsKey("IsLastInstance"))
                {
                    isLastInstance = Convert.ToBoolean(message.MessageAttributes["IsLastInstance"].StringValue);
                }

                int serviceProviderId = 0;
                if (message.MessageAttributes.ContainsKey("ServiceProviderId"))
                {
                    serviceProviderId = int.Parse(message.MessageAttributes["ServiceProviderId"].StringValue);
                }

                bool isOptLastStepSendEmail = false;
                if (message.MessageAttributes.ContainsKey("IsOptLastStepSendEmail"))
                {
                    isOptLastStepSendEmail = Convert.ToBoolean(message.MessageAttributes["IsOptLastStepSendEmail"].StringValue);
                }

                long sessionId = 0;
                if (message.MessageAttributes.ContainsKey("SessionId"))
                {
                    sessionId = long.Parse(message.MessageAttributes["SessionId"].StringValue);
                    LogInfo(context, "INFO", $"SessionId: {sessionId}");
                }

                LogInfo(context, "SUB", $"InstanceId: {instanceId}, RetryCount: {retryCount}");
                var optimizationQueueLength = GetOptimizationQueueLength(context);

                if (isOptLastStepSendEmail)
                {
                    // process send email
                    OptCustomerSendEmail(context, instanceId, sessionId, serviceProviderId, retryCount);
                    return;
                }

                if (optimizationQueueLength == 0)
                {
                    try
                    {
                        CleanupInstance(context, instanceId, isCustomerOptimization, isLastInstance, serviceProviderId);
                    }
                    catch (Exception ex)
                    {
                        LogInfo(context, "WARN", $"Error occurred on cleanup, requeuing: {ex.Message}");
                        RequeueCleanup(context, instanceId, retryCount, optimizationQueueLength, isCustomerOptimization);
                    }
                }
                else if (retryCount < 10)
                {
                    RequeueCleanup(context, instanceId, retryCount, optimizationQueueLength, isCustomerOptimization);
                }
                else
                {
                    LogInfo(context, "EXCEPTION", $"Optimization Cleanup Timed Out. Too many retry attempts.");
                }
            }
            else
            {
                LogInfo(context, "EXCEPTION", $"No Instance Id provided in message");
            }
        }

        private void OptCustomerSendEmail(KeySysLambdaContext context, long instanceId, long sessionId, int serviceProviderId, int retryCount)
        {
            LogInfo(context, CommonConstants.SUB, "");

            var instance = GetInstance(context, instanceId);
            //check optimization customer
            var checkOptProcessing = CheckOptCustomerProcessing(context, serviceProviderId, sessionId);
            if (checkOptProcessing)
            {
                LogInfo(context, CommonConstants.SUB, "Customer Optimization process has not finish yet.");
                if (retryCount <= _cleanUpSendEmailRetryCount)
                {
                    QueueLastStepOptCustomerCleanup(context, instanceId, sessionId, true, serviceProviderId, _optCustomerCleanUpDelaySeconds, retryCount + 1);
                }
                else
                {
                    LogInfo(context, CommonConstants.WARNING, $"Customer Optimization process has retried {_cleanUpSendEmailRetryCount} times.");
                }
                return;
            }
            OptimizationCustomerEndProcess jsonContent;
            if (instance.PortalType == PortalTypes.CrossProvider)
            {
                jsonContent = new OptimizationCustomerEndProcess()
                {
                    InstanceId = instanceId,
                    SessionId = sessionId,
                    ServiceProviderId = serviceProviderId,
                    SiteType = (int)instance.CustomerType,
                    DetailLastSyncDate = null,
                    UsageLastSyncDate = null,
                    BillingPeriodEndDate = instance.BillingPeriodEndDate,
                    TenantId = instance.TenantId
                };
            }
            else
            {
                var integrationType = (IntegrationType)instance.IntegrationId.GetValueOrDefault();

                // get sync results
                var syncResults = GetSummaryValues(context,
                    integrationType,
                    instance.ServiceProviderId.GetValueOrDefault());
                jsonContent = new OptimizationCustomerEndProcess()
                {
                    InstanceId = instanceId,
                    SessionId = sessionId,
                    ServiceProviderId = serviceProviderId,
                    SiteType = (int)instance.CustomerType,
                    DetailLastSyncDate = syncResults.DetailLastSyncDate,
                    UsageLastSyncDate = syncResults.UsageLastSyncDate,
                    BillingPeriodEndDate = instance.BillingPeriodEndDate,
                    TenantId = instance.TenantId
                };
            }

            var result = new AWS.Core.Models.ProxyResultBase();

            using (var client = new HttpClient(new LambdaLoggingHandler()))
            {
                if (!string.IsNullOrWhiteSpace(_proxyUrl))
                {
                    var payload = new Altaworx.AWS.Core.Models.PayloadModel()
                    {
                        JsonContent = JsonConvert.SerializeObject(jsonContent),
                        Password = null,
                        Token = null,
                        Username = null,
                        IsOptCustomerSendEmail = true
                    };

                    LogInfo(context, CommonConstants.INFO, "Call API Proxy AMOP to send email");
                    result = client.OptCustomerSendEmailProxy(_proxyUrl, payload, context.logger);
                }
            }

            LogInfo(context, CommonConstants.INFO, $"Call API Proxy AMOP: {result.IsSuccessful}");
            if (result.IsSuccessful)
            {
                // clear data from processing table 
                DeleteDataFromOptCustomerProcessing(context, serviceProviderId, instance.SessionId.Value);
            }
        }

        private int GetOptimizationQueueLength(KeySysLambdaContext context)
        {
            var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var request = new GetQueueAttributesRequest(_watchQueueUrl, new List<string> { "ApproximateNumberOfMessages", "ApproximateNumberOfMessagesDelayed", "ApproximateNumberOfMessagesNotVisible" });
                var response = client.GetQueueAttributesAsync(request);
                response.Wait();
                if (response.Status == TaskStatus.Faulted || response.Status == TaskStatus.Canceled)
                {
                    LogInfo(context, "RESPONSE STATUS", $"Error Getting Queue Length: {response.Status}");
                    return int.MaxValue;
                }

                var queueLength = response.Result.ApproximateNumberOfMessages + response.Result.ApproximateNumberOfMessagesDelayed + response.Result.ApproximateNumberOfMessagesNotVisible;
                return queueLength;
            }
        }

        private void CleanupInstance(KeySysLambdaContext context, long instanceId, bool isCustomerOptimization, bool isLastInstance, int serviceProviderId)
        {
            LogInfo(context, "SUB", $"CleanupInstance");
            LogInfo(context, "SUB", $"instanceId: {instanceId}");
            LogInfo(context, "SUB", $"isCustomerOptimization: {isCustomerOptimization}");
            LogInfo(context, "SUB", $"isLastInstance: {isLastInstance}");
            LogInfo(context, "SUB", $"serviceProviderId: {serviceProviderId}");

            // get instance
            var instance = GetInstance(context, instanceId);

            //check if instance is found & has valid status for process
            if (instance.Id <= 0)
            {
                LogInfo(context, "EXCEPTION", $"Could not find instance with id {instanceId}.");
                return;
            }

            if (INSTANCE_FINISHED_STATUSES.Contains((OptimizationStatus)instance.RunStatusId))
            {
                LogInfo(context, "WARNING", $"Duplicated instance cleanup request for instance with id {instanceId}.");
                return;
            }

            // get billing period
            var carrierBillingPeriod = new BillingPeriod(instance.BillingPeriodIdByPortalType.GetValueOrDefault(), instance.ServiceProviderId.GetValueOrDefault(), instance.BillingPeriodEndDate.Year, instance.BillingPeriodEndDate.Month, instance.BillingPeriodEndDate.Day, instance.BillingPeriodEndDate.Hour, context.OptimizationSettings.BillingTimeZone, instance.BillingPeriodEndDate);

            // get comm groups
            var commGroups = GetCommGroups(context, instanceId);

            // get integration types
            var integrationTypeRepository = new IntegrationTypeRepository(context.ConnectionString);
            var integrationTypes = integrationTypeRepository.GetIntegrationTypes();

            // email attachment paths
            var queueIds = new List<long>();

            // cleanup each comm group
            foreach (var commGroup in commGroups)
            {
                // get winning queue for each comm group
                var winningQueueId = GetWinningQueueId(context, commGroup.Id);

                // end all queues that aren't ended
                EndQueuesForCommGroup(context, commGroup.Id);

                // clean up all results that aren't winners
                CleanupDeviceResultsForCommGroup(context, commGroup.Id, winningQueueId);

                // add queue id
                queueIds.Add(winningQueueId);
            }

            // end instance
            var endTime = StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithSuccess);
            instance.RunEndTime = endTime;

            var usesProration = false;
            if (queueIds.Count > 0)
            {
                var queue = GetQueue(context, queueIds.First());
                usesProration = queue.UsesProration;
            }

            // write results to bytes
            var fileResult = WriteResultByPortalType(context, isCustomerOptimization, instance, carrierBillingPeriod, queueIds, usesProration);
            if (instance.PortalType == PortalTypes.CrossProvider)
            {
                ProcessResultForCrossProvider(context, isCustomerOptimization, isLastInstance, instance, fileResult);
            }
            else
            {
                ProcessResultForSingleServiceProvider(context, isCustomerOptimization, isLastInstance, serviceProviderId, instance, integrationTypes, fileResult);
            }

        }

        private void ProcessResultForSingleServiceProvider(KeySysLambdaContext context, bool isCustomerOptimization, bool isLastInstance, int serviceProviderId, OptimizationInstance instance, IList<IntegrationTypeModel> integrationTypes, OptimizationInstanceResultFile fileResult)
        {
            var integrationType = (IntegrationType)instance.IntegrationId.GetValueOrDefault();
            // get sync results
            var syncResults = GetSummaryValues(context,
                integrationType,
                instance.ServiceProviderId.GetValueOrDefault());

            // send results by email
            var billingTimeZone = context.OptimizationSettings.BillingTimeZone;
            if (isCustomerOptimization)
            {
                OptimizationCustomerSendResults(context, instance, syncResults, isLastInstance, serviceProviderId);
            }
            else
            {
                SendResults(context, instance, fileResult.AssignmentXlsxBytes, billingTimeZone,
                    syncResults, integrationType, integrationTypes);
            }

            // queue rate plan update (if auto-update rate plans)
            if ((integrationType == IntegrationType.Jasper
                || integrationType == IntegrationType.POD19
                || integrationType == IntegrationType.TMobileJasper
                || integrationType == IntegrationType.Rogers)
                && context.OptimizationSettings.CanAutoUpdateRatePlans && instance.RevCustomerId == null && !instance.AMOPCustomerId.HasValue)
            {
                // get rate plan update count for this instance
                var connectionString = context.ConnectionString;
                var logger = context.logger;
                var ratePlansToUpdateCount = CountRatePlansToUpdate(instance.Id, connectionString, logger);
                if (DoesHaveTimeToProcessRatePlanUpdates(instance, ratePlansToUpdateCount, connectionString,
                    logger, DateTime.UtcNow, billingTimeZone))
                {
                    // queue rate plans
                    QueueRatePlanUpdates(context, instance.Id, instance.TenantId);

                    // send "go" rate plan update email
                    SendGoForRatePlanUpdatesEmail(context, instance, billingTimeZone);
                }
                else
                {
                    // send "no go" rate plan update email
                    SendNoGoForRatePlanUpdatesEmail(context, instance, billingTimeZone);
                }
            }
        }

        private OptimizationInstanceResultFile WriteResultByPortalType(KeySysLambdaContext context, bool isCustomerOptimization, OptimizationInstance instance, BillingPeriod billingPeriod, List<long> queueIds, bool usesProration)
        {
            if (instance.PortalType == PortalTypes.Mobility)
            {
                return WriteMobilityResultsByOptimizationType(context, instance, queueIds, billingPeriod, usesProration, isCustomerOptimization);
            }
            else if (instance.PortalType == PortalTypes.M2M)
            {
                return WriteM2MResults(context, instance, queueIds, billingPeriod, usesProration, isCustomerOptimization);
            }
            else if (instance.PortalType == PortalTypes.CrossProvider)
            {
                // Cross-Provider optimization currently only have one type which is Customer Optimization
                return WriteCrossProviderCustomerResults(context, instance, queueIds, usesProration);
            }
            else
            {
                OptimizationErrorHandler.OnPortalTypeError(context, PortalType, true);
                return null;
            }
        }

        public static bool DoesHaveTimeToProcessRatePlanUpdates(OptimizationInstance instance, int ratePlansToUpdateCount,
            string connectionString, IKeysysLogger logger, DateTime currentSystemTimeUtc, TimeZoneInfo timeZoneInfo)
        {
            logger.LogInfo("SUB", $"DoesHaveTimeToProcessRatePlanUpdates({instance.Id})");

            // get rate plan update summary for previous instance
            var ratePlanUpdateSummaryRecords = GetPreviousRatePlanUpdateSummary(instance.Id, connectionString, logger);

            // get minutes remaining in bill cycle
            decimal minutesRemainingInBillCycle = MinutesRemainingInBillCycle(logger, instance.BillingPeriodEndDate, currentSystemTimeUtc, timeZoneInfo);

            // get est minutes to update rate plans
            var minutesToUpdateRatePlans = MinutesToUpdateRatePlans(ratePlansToUpdateCount, ratePlanUpdateSummaryRecords, logger);

            // check if there is time to do the updates (leave a buffer of 10 minutes)
            if (minutesRemainingInBillCycle > 0 && minutesRemainingInBillCycle - minutesToUpdateRatePlans >= 10)
            {
                return true;
            }

            // default to no
            return false;
        }

        private static List<OptimizationRatePlanUpdateSummary> GetPreviousRatePlanUpdateSummary(long instanceId, string connectionString, IKeysysLogger logger)
        {
            logger.LogInfo("SUB", $"GetPreviousRatePlanUpdateSummary({instanceId})");

            var summaryRecords = new List<OptimizationRatePlanUpdateSummary>();
            using (var conn = new SqlConnection(connectionString))
            {
                using (var cmd = new SqlCommand("usp_Optimization_PreviousRatePlanUpdateSummary", conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@InstanceId", instanceId);

                    conn.Open();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var ratePlanSummaryRecord = RatePlanSummaryRecordFromReader(reader);
                            summaryRecords.Add(ratePlanSummaryRecord);
                        }
                    }
                }
            }

            return summaryRecords;
        }

        private static OptimizationRatePlanUpdateSummary RatePlanSummaryRecordFromReader(IDataRecord reader)
        {
            return new OptimizationRatePlanUpdateSummary
            {
                Id = long.Parse(reader["Id"].ToString()),
                InstanceId = long.Parse(reader["InstanceId"].ToString()),
                QueueCount = int.Parse(reader["QueueCount"].ToString()),
                MinSecondsToUpdate = int.Parse(reader["MinSecondsToUpdate"].ToString()),
                MaxSecondsToUpdate = int.Parse(reader["MaxSecondsToUpdate"].ToString()),
                AvgSecondsToUpdate = decimal.Parse(reader["AvgSecondsToUpdate"].ToString()),
                UpdateRateDevicesPerMinute = decimal.Parse(reader["UpdateRateDevicesPerMinute"].ToString())
            };
        }

        private static int CountRatePlansToUpdate(long instanceId, string connectionString, IKeysysLogger logger)
        {
            logger.LogInfo("SUB", $"CountRatePlansToUpdate({instanceId})");

            var ratePlansToUpdate = 0;
            using (var conn = new SqlConnection(connectionString))
            {
                using (var cmd = new SqlCommand("usp_Optimization_RatePlanChangeCount", conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@InstanceId", instanceId);

                    conn.Open();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            ratePlansToUpdate = int.Parse(reader["TargetDeviceCount"].ToString());
                        }
                    }
                }
            }

            return ratePlansToUpdate;
        }

        public static int MinutesRemainingInBillCycle(IKeysysLogger logger, DateTime billingPeriodEndDate, DateTime currentSystemTimeUtc, TimeZoneInfo timeZoneInfo)
        {
            logger.LogInfo("SUB", $"MinutesRemainingInBillCycle({billingPeriodEndDate})");

            var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentSystemTimeUtc, timeZoneInfo);

            return MinutesRemainingInBillCycle(logger, billingPeriodEndDate, currentLocalTime);
        }

        public static int MinutesRemainingInBillCycle(IKeysysLogger logger, DateTime billingPeriodEndDate, DateTime currentLocalTime)
        {
            logger.LogInfo("SUB", $"MinutesRemainingInBillCycle({billingPeriodEndDate},{currentLocalTime})");

            double totalSecondsRemaining = 0;
            if (currentLocalTime < billingPeriodEndDate)
            {
                totalSecondsRemaining = billingPeriodEndDate.Subtract(currentLocalTime).TotalSeconds;
            }

            return (int)Math.Floor(totalSecondsRemaining / 60); // 60 seconds in a minute
        }

        private static decimal MinutesToUpdateRatePlans(int ratePlansToUpdateCount,
            IReadOnlyCollection<OptimizationRatePlanUpdateSummary> ratePlanUpdateSummaryRecords,
            IKeysysLogger logger)
        {
            logger.LogInfo("SUB", $"MinutesToUpdateRatePlans({ratePlansToUpdateCount})");

            // rate plan update batch size
            var maxBatchSize = 250;

            // use max rate or 1/second => 60/min
            var maxUpdateRate = ratePlanUpdateSummaryRecords.Count > 0 ? ratePlanUpdateSummaryRecords.Max(x => x.UpdateRateDevicesPerMinute) : 60.0M;
            if (ratePlansToUpdateCount > maxBatchSize)
            {
                return maxBatchSize / maxUpdateRate;
            }
            else
            {
                return ratePlansToUpdateCount / maxUpdateRate;
            }
        }

        protected OptimizationInstanceResultFile WriteMobilityResultsByOptimizationType(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration, bool isCustomerOptimization)
        {
            if (isCustomerOptimization)
            {
                return WriteMobilityResults(context, instance, queueIds, billingPeriod, usesProration, isCustomerOptimization);
            }
            else
            {
                return WriteMobilityCarrierResults(context, instance, queueIds, billingPeriod, usesProration);
            }
        }

        protected OptimizationInstanceResultFile WriteMobilityResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration, bool isCustomerOptimization)
        {
            LogInfo(context, LogTypeConstant.Sub, $"(,{instance.Id},{string.Join(',', queueIds)})");
            var result = new MobilityOptimizationResult();
            var crossCustomerResult = new MobilityOptimizationResult();
            // only 1 billInAdvance Queue per instance
            var billInAdvanceQueue = GetBillInAdvanceQueueFromInstance(context, instance.Id);
            LogInfo(context, LogTypeConstant.Info, $"Bill in advance queue id: {billInAdvanceQueue.Id}");

            // get rate pools
            var crossOptimizationResultRatePools = GetResultRatePools(context, instance, billingPeriod, usesProration, queueIds, isCustomerOptimization);

            // create another set of rate pools
            var optimizationResultRatePools = GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools);

            AddUnassignedRatePool(context, instance, billingPeriod, usesProration, crossOptimizationResultRatePools, optimizationResultRatePools);

            foreach (var queueId in queueIds)
            {
                LogInfo(context, LogTypeConstant.Info, $"Building results for queue with id: {queueId}.");
                // get results for queue id
                var deviceResults = GetMobilityResults(context, new List<long>() { queueId }, billingPeriod);

                // build optimization result
                result = BuildMobilityOptimizationResult(deviceResults, optimizationResultRatePools, result);
                var sharedPooldeviceResults = GetMobilitySharedPoolResults(context, new List<long>() { queueId }, billingPeriod);
                sharedPooldeviceResults.AddRange(deviceResults);
                crossCustomerResult = BuildMobilityOptimizationResult(sharedPooldeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true);
            }

            // write result to stat file
            var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result);

            // write result to device output file (text)
            var assignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(result);
            byte[] sharedPoolStatFileBytes = null;
            byte[] sharedPoolAssignmentFileBytes = null;

            if (crossCustomerResult.CombinedRatePools.TotalSimCardCount > result.CombinedRatePools.TotalSimCardCount)
            {
                // write shared pool result to stat file
                sharedPoolStatFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, crossCustomerResult);

                // write shared pool result to device output file (text)
                sharedPoolAssignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(crossCustomerResult);
            }

            // write result to device output file (xlsx)
            LogInfo(context, "SUB", $"GenerateExcelFileFromByteArrays({result.QueueId})");
            var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);

            // save to database
            return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes);
        }

        protected OptimizationInstanceResultFile WriteMobilityCarrierResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration)
        {
            LogInfo(context, CommonConstants.SUB, $"(,{instance.Id},{string.Join(',', queueIds)})");

            // Get Rate Plans from QueueIds
            var ratePlans = carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());
            // Get Optimization Groups
            var optimizationGroups = carrierRatePlanRepository.GetValidOptimizationGroupsWithRatePlanIds(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());

            var deviceAssignments = new List<MobilityCarrierAssignmentExportModel>();
            var summariesByRatePlans = new List<MobilityCarrierSummaryReportModel>();
            // Get the device results
            var deviceResults = optimizationMobilityDeviceRepository.GetMobilityDeviceResults(context, queueIds, billingPeriod);
            if (deviceResults.Any(x => x.RatePlanTypeId == null || x.OptimizationGroupId == null))
            {
                LogInfo(context, CommonConstants.ERROR, string.Format(LogCommonStrings.ERROR_NULL_RATE_PLAN_TYPE_ID_OPTIMIZATION_GROUP_ID, string.Join(',', deviceResults.Select(x => x.ICCID))));
            }
            var deviceResultsByOptimizationGroups = deviceResults
                .Where(x => x.RatePlanTypeId != null && x.OptimizationGroupId != null)
                .GroupBy(x => x.OptimizationGroupId)
                .ToDictionary(x => x.Key, x => x.ToList());
            // Map the devices to each optimization group
            foreach (var optimizationGroup in optimizationGroups)
            {
                if (!deviceResultsByOptimizationGroups.TryGetValue(optimizationGroup.Id, out var groupDeviceResults))
                {
                    LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.NO_DEVICE_FOUND_FOR_OPTIMIZATION_GROUP_ID, optimizationGroup.Id));
                    continue;
                }
                var groupRatePlans = MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup);
                var optimizationGroupResultPools = new List<ResultRatePool>();
                foreach (var ratePlan in groupRatePlans)
                {
                    optimizationGroupResultPools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, ResultRatePoolKeyType.ICCID, optimizationGroup.Name));
                }
                // Calculate starting cost per device
                // Might need to increase RAM is we are calculating again
                var originalRatePools = RatePoolFactory.CreateRatePools(ratePlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
                var originalAssignmentCollection = RatePoolCollectionFactory.CreateRatePoolCollection(originalRatePools, shouldPoolByOptimizationGroup: true);

                foreach (SimCardResult deviceResult in groupDeviceResults)
                {
                    // Add device to the original assignment collection
                    foreach (var ratePool in originalAssignmentCollection.RatePools)
                    {
                        if (ratePool.RatePlan.Id == deviceResult.StartingRatePlanId)
                        {
                            ratePool.AddSimCard(deviceResult.ToSimCard());
                            break;
                        }
                    }
                    // Add device to the final result collection
                    foreach (var ratePool in optimizationGroupResultPools)
                    {
                        if (ratePool.RatePlan.Id == deviceResult.RatePlanId)
                        {
                            ratePool.AddSimCard(deviceResult);
                            break;
                        }
                    }
                }
                deviceAssignments.AddRange(MapToMobilityDeviceAssignmentsFromResult(originalAssignmentCollection, optimizationGroupResultPools, billingPeriod, optimizationGroup));
                summariesByRatePlans.AddRange(MapToSummariesFromResult(optimizationGroupResultPools, optimizationGroup));
            }
            // Write result to device output file (xlsx)
            var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(deviceAssignments, summariesByRatePlans);

            // save to database
            return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes);
        }

        private List<MobilityCarrierSummaryReportModel> MapToSummariesFromResult(List<ResultRatePool> optimizationGroupResultPools, OptimizationGroup optimizationGroup)
        {
            var summaries = new List<MobilityCarrierSummaryReportModel>();
            foreach (var resultPool in optimizationGroupResultPools)
            {
                summaries.Add(MobilityCarrierSummaryReportModel.FromResultPool(resultPool, optimizationGroup));
            }
            return summaries;
        }

        private List<MobilityCarrierAssignmentExportModel> MapToMobilityDeviceAssignmentsFromResult(RatePoolCollection originalAssignmentCollection, List<ResultRatePool> optimizationGroupResultPools, BillingPeriod billingPeriod, OptimizationGroup optimizationGroup)
        {
            var deviceAssignments = new List<MobilityCarrierAssignmentExportModel>();
            foreach (var resultPool in optimizationGroupResultPools)
            {
                foreach (var sim in resultPool.SimCards)
                {
                    var originalRatePool = originalAssignmentCollection.RatePools.FirstOrDefault(x => x.SimCards.TryGetValue(sim.Key, out var _));
                    if (originalRatePool == null)
                    {
                        continue;
                    }
                    var deviceAssignment = MobilityCarrierAssignmentExportModel.FromSimCardResult(sim.Value, originalRatePool?.RatePlan, resultPool.RatePlan, billingPeriod.BillingPeriodStart, optimizationGroup.Name);
                    deviceAssignments.Add(deviceAssignment);
                }
            }
            return deviceAssignments;
        }

        protected OptimizationInstanceResultFile WriteM2MResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration, bool isCustomerOptimization)
        {
            LogInfo(context, LogTypeConstant.Sub, $"(,instance.Id: {instance.Id}, queueIds: {string.Join(',', queueIds)})");
            M2MOptimizationResult result = new M2MOptimizationResult();
            M2MOptimizationResult crossCustomerResult = new M2MOptimizationResult();

            // get rate pools
            var crossOptimizationResultRatePools = GetResultRatePools(context, instance, billingPeriod, usesProration, queueIds, isCustomerOptimization);

            // create another set of rate pools
            var optimizationResultRatePools = GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools);

            AddUnassignedRatePool(context, instance, billingPeriod, usesProration, crossOptimizationResultRatePools, optimizationResultRatePools);

            var shouldShowCrossPoolingTab = false;
            foreach (var queueId in queueIds)
            {
                LogInfo(context, LogTypeConstant.Info, $"Building results for queue with id: {queueId}.");
                // get results for queue id
                var deviceResults = GetM2MResults(context, new List<long>() { queueId }, billingPeriod);

                // build optimization result
                result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
                var sharedPoolDeviceResults = GetM2MSharedPoolResults(context, new List<long>() { queueId }, billingPeriod);
                if (sharedPoolDeviceResults != null && sharedPoolDeviceResults.Count > 0)
                {
                    shouldShowCrossPoolingTab = true;
                }
                sharedPoolDeviceResults.AddRange(deviceResults);
                // enable shouldSkipAutoChangeRatePlans flag to not show rate plans & devices with "Auto Change Rate Plan" in second tab
                crossCustomerResult = BuildM2MOptimizationResult(sharedPoolDeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true);
            }

            // write result to stat file
            var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result);

            // write result to device output file (text)
            var assignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(result);
            byte[] sharedPoolStatFileBytes = null;
            byte[] sharedPoolAssignmentFileBytes = null;

            if (shouldShowCrossPoolingTab)
            {
                // write shared pool result to stat file
                sharedPoolStatFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, crossCustomerResult);

                // write shared pool result to device output file (text)
                sharedPoolAssignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(crossCustomerResult);
            }

            // write result to device output file (xlsx)
            LogInfo(context, "SUB", $"GenerateExcelFileFromByteArrays({result.QueueId})");
            var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);

            // save to database
            return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes);
        }

        private static void AddUnassignedRatePool(KeySysLambdaContext context, OptimizationInstance instance, BillingPeriod billingPeriod, bool usesProration, List<ResultRatePool> crossOptimizationResultRatePools, List<ResultRatePool> optimizationResultRatePools = null)
        {
            LogInfo(context, LogTypeConstant.Sub, $"(,{instance.Id},crossOptimizationResultRatePools.Count: {crossOptimizationResultRatePools?.Count},optimizationResultRatePools.Count: {optimizationResultRatePools?.Count})");
            // check if customer optimization
            if ((instance.RevCustomerId != null || instance.AMOPCustomerId.HasValue))
            {
                // add unassigned rate pool
                LogInfo(context, LogTypeConstant.Info, $"Is customer optimization. Adding unassigned rate plan.");
                var unassignedRatePlan = RatePlan.CreateUnassignedRatePlan();

                optimizationResultRatePools?.Add(new ResultRatePool(unassignedRatePlan, usesProration, billingPeriod, instance.RatePoolKeyType, string.Empty));

                crossOptimizationResultRatePools?.Add(new ResultRatePool(unassignedRatePlan, usesProration, billingPeriod, instance.RatePoolKeyType, string.Empty));
            }
        }

        private static List<ResultRatePool> GenerateCustomerSpecificRatePools(List<ResultRatePool> crossOptimizationResultRatePools)
        {
            var optimizationResultRatePools = new List<ResultRatePool>();
            crossOptimizationResultRatePools.ForEach(crossOptimizationResultRatePool =>
            {
                if (!crossOptimizationResultRatePool.IsSharedRatePool)
                {
                    optimizationResultRatePools.Add(new ResultRatePool(crossOptimizationResultRatePool));
                }
            });
            return optimizationResultRatePools;
        }

        private List<SimCardResult> GetM2MResults(KeySysLambdaContext context, List<long> queueIds, BillingPeriod billingPeriod)
        {
            LogInfo(context, LogTypeConstant.Sub, $"({string.Join(',', queueIds)},{billingPeriod.Id})");
            try
            {
                var simCards = new List<SimCardResult>();
                if (queueIds.Count == 0)
                {
                    LogInfo(context, LogTypeConstant.Warning, $"No queue Id to process.");
                    return simCards;
                }
                using (var conn = new SqlConnection(context.ConnectionString))
                {
                    conn.Open();

                    using (var cmd = new SqlCommand(@"
                            SELECT device.[Id] AS DeviceId, 
                                [UsageMB], device.[ICCID], device.[MSISDN],
                                ISNULL(commPlan.[AliasName], device.[CommunicationPlan]) AS CommunicationPlan, 
                                ISNULL(carrierPlan.[RatePlanCode],  customerPlan.[RatePlanCode]) AS RatePlanCode, 
                                ISNULL(deviceResult.[AssignedCustomerRatePlanId], deviceResult.[AssignedCarrierRatePlanId]) AS RatePlanId, 
                                deviceResult.[CustomerRatePoolId] AS RatePoolId,
                                customerPool.[Name] AS RatePoolName,
                                [ChargeAmt], device.[ProviderDateActivated], [SmsUsage], 
                                [SmsChargeAmount], deviceResult.[BaseRateAmt], deviceResult.[RateChargeAmt], deviceResult.[OverageChargeAmt] 
                            FROM OptimizationDeviceResult deviceResult 
                            INNER JOIN Device device ON deviceResult.[AmopDeviceId] = device.[Id] 
                            LEFT JOIN JasperCommunicationPlan commPlan ON commPlan.[CommunicationPlanName] = device.[CommunicationPlan] 
                            LEFT JOIN JasperCarrierRatePlan carrierPlan ON deviceResult.[AssignedCarrierRatePlanId] = carrierPlan.[Id] 
                            LEFT JOIN JasperCustomerRatePlan customerPlan ON deviceResult.[AssignedCustomerRatePlanId] = customerPlan.[Id] 
                            LEFT JOIN CustomerRatePool customerPool ON deviceResult.[CustomerRatePoolId] = customerPool.[Id] 
                            WHERE deviceResult.[QueueId] IN (@QueueIds)", conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.CommandTimeout = SQLConstant.ShortTimeoutSeconds;
                        cmd.AddArrayParameters("@QueueIds", queueIds);

                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            var simCard = SimCardResultFromReader(rdr, billingPeriod);
                            simCards.Add(simCard);
                        }
                    }
                }

                return simCards;
            }
            catch (SqlException ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when executing stored procedure: {ex.Message}, ErrorCode:{ex.ErrorCode}-{ex.Number}, Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (InvalidOperationException ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when connecting to database: {ex.Message}, Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (Exception ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when getting specific customers M2M devices for queue Ids {string.Join(',', queueIds)} : {ex.Message}, Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private List<SimCardResult> GetMobilityResults(KeySysLambdaContext context, List<long> queueIds, BillingPeriod billingPeriod)
        {
            var simCards = new List<SimCardResult>();

            foreach (var queueId in queueIds)
            {
                using (var conn = new SqlConnection(context.ConnectionString))
                {
                    conn.Open();

                    using (var cmd = new SqlCommand(
                        @"SELECT jd.Id AS DeviceId, UsageMB, jd.ICCID, jd.MSISDN, '' AS CommunicationPlan,
                                    ISNULL(jcarr_rp.RatePlanCode,  jcust_rp.RatePlanCode) AS RatePlanCode,
                                    ISNULL(odr.AssignedCustomerRatePlanId, odr.AssignedCarrierRatePlanId) AS RatePlanId, 
                                    odr.CustomerRatePoolId AS RatePoolId,
                                    cust_pool.[Name] AS RatePoolName,
                                    ChargeAmt, jd.ProviderDateActivated, SmsUsage, SmsChargeAmount,
                                    odr.BaseRateAmt, odr.RateChargeAmt, odr.OverageChargeAmt 
                                FROM OptimizationMobilityDeviceResult odr 
                                INNER JOIN MobilityDevice jd ON odr.AmopDeviceId = jd.Id 
                                LEFT JOIN JasperCarrierRatePlan jcarr_rp ON odr.AssignedCarrierRatePlanId = jcarr_rp.Id 
                                LEFT JOIN JasperCustomerRatePlan jcust_rp ON odr.AssignedCustomerRatePlanId = jcust_rp.Id 
                                LEFT JOIN CustomerRatePool cust_pool ON odr.CustomerRatePoolId = cust_pool.Id 
                                WHERE QueueId = @queueId", conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.Parameters.AddWithValue("@queueId", queueId);

                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            var simCard = SimCardResultFromReader(rdr, billingPeriod);
                            simCards.Add(simCard);
                        }
                    }
                }
            }

            return simCards;
        }

        private List<SimCardResult> GetM2MSharedPoolResults(KeySysLambdaContext context, List<long> queueIds, BillingPeriod billingPeriod)
        {
            LogInfo(context, LogTypeConstant.Sub, $"({string.Join(',', queueIds)},{billingPeriod.Id})");
            try
            {
                var simCards = new List<SimCardResult>();
                if (queueIds.Count == 0)
                {
                    LogInfo(context, LogTypeConstant.Warning, $"No queue Id to process.");
                    return simCards;
                }
                using (var conn = new SqlConnection(context.ConnectionString))
                {
                    conn.Open();

                    using (var cmd = new SqlCommand(@"
                            SELECT device.[Id] AS DeviceId, 
                                [UsageMB], device.[ICCID], device.[MSISDN],
                                ISNULL(commPlan.[AliasName], device.[CommunicationPlan]) AS CommunicationPlan, 
                                ISNULL(carrierPlan.[RatePlanCode],  customerPlan.[RatePlanCode]) AS RatePlanCode, 
                                ISNULL(deviceResult.[AssignedCustomerRatePlanId], deviceResult.[AssignedCarrierRatePlanId]) AS RatePlanId, 
                                deviceResult.[CustomerRatePoolId] AS RatePoolId,
                                customerPool.[Name] AS RatePoolName,
                                [ChargeAmt], device.[ProviderDateActivated], [SmsUsage], 
                                [SmsChargeAmount], deviceResult.[BaseRateAmt], deviceResult.[RateChargeAmt], deviceResult.[OverageChargeAmt] 
                            FROM OptimizationSharedPoolResult deviceResult 
                            INNER JOIN Device device ON deviceResult.[AmopDeviceId] = device.[Id] 
                            LEFT JOIN JasperCommunicationPlan commPlan ON commPlan.[CommunicationPlanName] = device.[CommunicationPlan] 
                            LEFT JOIN JasperCarrierRatePlan carrierPlan ON deviceResult.[AssignedCarrierRatePlanId] = carrierPlan.[Id] 
                            LEFT JOIN JasperCustomerRatePlan customerPlan ON deviceResult.[AssignedCustomerRatePlanId] = customerPlan.[Id] 
                            LEFT JOIN CustomerRatePool customerPool ON deviceResult.[CustomerRatePoolId] = customerPool.[Id] 
                            WHERE deviceResult.[QueueId] IN (@QueueIds)", conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.CommandTimeout = SQLConstant.ShortTimeoutSeconds;
                        cmd.AddArrayParameters("@QueueIds", queueIds);

                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            var simCard = SimCardResultFromReader(rdr, billingPeriod);
                            simCards.Add(simCard);
                        }
                    }
                }

                return simCards;
            }
            catch (SqlException ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when executing stored procedure: {ex.Message}, ErrorCode:{ex.ErrorCode}-{ex.Number}, Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (InvalidOperationException ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when connecting to database: {ex.Message}, Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (Exception ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when getting M2M cross customer devices for queue Ids {string.Join(',', queueIds)} : {ex.Message}, Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private List<SimCardResult> GetMobilitySharedPoolResults(KeySysLambdaContext context, List<long> queueIds, BillingPeriod billingPeriod)
        {
            LogInfo(context, LogTypeConstant.Sub, $"({string.Join(',', queueIds)},{billingPeriod.Id})");
            var simCards = new List<SimCardResult>();

            foreach (var queueId in queueIds)
            {
                using (var conn = new SqlConnection(context.ConnectionString))
                {
                    conn.Open();

                    using (var cmd = new SqlCommand(@"
                                SELECT jd.Id AS DeviceId, UsageMB, jd.ICCID, jd.MSISDN, '' AS CommunicationPlan, 
                                    ISNULL(jcarr_rp.RatePlanCode,  jcust_rp.RatePlanCode) AS RatePlanCode, 
                                    ISNULL(odr.AssignedCustomerRatePlanId, odr.AssignedCarrierRatePlanId) AS RatePlanId, 
                                    odr.CustomerRatePoolId AS RatePoolId,
                                    cust_pool.[Name] AS RatePoolName,
                                    ChargeAmt, jd.ProviderDateActivated, SmsUsage, 
                                    SmsChargeAmount, odr.BaseRateAmt, odr.RateChargeAmt, odr.OverageChargeAmt 
                                FROM OptimizationMobilitySharedPoolResult odr 
                                INNER JOIN MobilityDevice jd ON odr.AmopDeviceId = jd.Id 
                                LEFT JOIN JasperCarrierRatePlan jcarr_rp ON odr.AssignedCarrierRatePlanId = jcarr_rp.Id 
                                LEFT JOIN JasperCustomerRatePlan jcust_rp ON odr.AssignedCustomerRatePlanId = jcust_rp.Id 
                                LEFT JOIN CustomerRatePool cust_pool ON odr.CustomerRatePoolId = cust_pool.Id 
                                WHERE QueueId = @queueId", conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.Parameters.AddWithValue("@queueId", queueId);

                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            var simCard = SimCardResultFromReader(rdr, billingPeriod);
                            simCards.Add(simCard);
                        }
                    }
                }
            }

            return simCards;
        }

        private RatePlanPoolMapping RatePlanPoolMappingFromReader(SqlDataReader rdr)
        {
            var ratePlanId = string.Empty;
            if (rdr["RatePlanId"] != DBNull.Value)
            {
                ratePlanId = rdr["RatePlanId"].ToString();
            }

            var ratePoolId = string.Empty;
            if (rdr["RatePoolId"] != DBNull.Value)
            {
                ratePoolId = rdr["RatePoolId"].ToString();
            }

            var ratePoolName = string.Empty;
            if (rdr["RatePoolName"] != DBNull.Value)
            {
                ratePoolName = rdr["RatePoolName"].ToString();
            }
            return new RatePlanPoolMapping()
            {
                RatePlanId = !string.IsNullOrWhiteSpace(ratePlanId) ? Convert.ToInt32(ratePlanId) : 0,
                RatePoolId = !string.IsNullOrWhiteSpace(ratePoolId) ? Convert.ToInt32(ratePoolId) : 0,
                RatePoolName = ratePoolName,
            };
        }
        private SimCardResult SimCardResultFromReader(SqlDataReader rdr, BillingPeriod billingPeriod)
        {
            var usageMb = string.Empty;
            if (rdr["UsageMB"] != DBNull.Value)
            {
                usageMb = rdr["UsageMB"].ToString();
            }

            var ratePlanId = string.Empty;
            if (rdr["RatePlanId"] != DBNull.Value)
            {
                ratePlanId = rdr["RatePlanId"].ToString();
            }

            var ratePoolId = string.Empty;
            if (rdr["RatePoolId"] != DBNull.Value)
            {
                ratePoolId = rdr["RatePoolId"].ToString();
            }

            var ratePoolName = string.Empty;
            if (rdr["RatePoolName"] != DBNull.Value)
            {
                ratePoolName = rdr["RatePoolName"].ToString();
            }



            var simCard = new SimCardResult()
            {
                Id = Convert.ToInt32(rdr["DeviceId"].ToString()),
                CycleDataUsageMB = !string.IsNullOrWhiteSpace(usageMb) ? Convert.ToDecimal(usageMb) : 0.0M,
                ICCID = rdr["ICCID"].ToString(),
                MSISDN = rdr["MSISDN"].ToString(),
                CommunicationPlan = rdr["CommunicationPlan"].ToString(),
                RatePlanCode = rdr["RatePlanCode"].ToString(),
                RatePlanId = !string.IsNullOrWhiteSpace(ratePlanId) ? Convert.ToInt32(rdr["RatePlanId"].ToString()) : 0,
                RatePoolId = !string.IsNullOrWhiteSpace(ratePoolId) ? Convert.ToInt32(ratePoolId) : 0,
                RatePoolName = ratePoolName,
                ChargeAmt = Convert.ToDecimal(rdr["ChargeAmt"].ToString()),
                DateActivated = !rdr.IsDBNull("ProviderDateActivated") ? new DateTime?(rdr.GetDateTime("ProviderDateActivated")) : null,
                SmsUsage = !rdr.IsDBNull("SmsUsage") ? rdr.GetInt64("SmsUsage") : 0,
                SmsChargeAmount = !rdr.IsDBNull("SmsChargeAmount") ? rdr.GetDecimal("SmsChargeAmount") : 0,
                BaseRateAmount = !rdr.IsDBNull("BaseRateAmt") ? rdr.GetDecimal("BaseRateAmt") : 0,
                RateChargeAmount = !rdr.IsDBNull("RateChargeAmt") ? rdr.GetDecimal("RateChargeAmt") : 0,
                OverageChargeAmount = !rdr.IsDBNull("OverageChargeAmt") ? rdr.GetDecimal("OverageChargeAmt") : 0,
            };

            simCard.WasActivatedInThisBillingPeriod = DateIsInBillingPeriod(simCard.DateActivated, billingPeriod);
            simCard.DaysActivatedInBillingPeriod = simCard.WasActivatedInThisBillingPeriod
                ? DaysLeftInBillingPeriod(simCard.DateActivated, billingPeriod)
                : billingPeriod.DaysInBillingPeriod;

            return simCard;
        }

        private List<RatePlan> GetM2MRatePlans(KeySysLambdaContext context, OptimizationInstance instance, int billingPeriodId)
        {
            var ratePlans = new List<RatePlan>();
            if (instance.RevCustomerId != null)
            {
                ratePlans = GetCustomerRatePlans(context, instance.RevCustomerId.Value, billingPeriodId, instance.ServiceProviderId,
                    instance.TenantId, instance.CustomerType, instance.AMOPCustomerId);
            }
            else if (instance.AMOPCustomerId.HasValue)
            {
                ratePlans = GetCustomerRatePlans(context, Guid.Empty, billingPeriodId, instance.ServiceProviderId,
                    instance.TenantId, instance.CustomerType, instance.AMOPCustomerId);
            }
            else
            {
                ratePlans = GetNonRetiredRatePlans(context, instance.ServiceProviderId.GetValueOrDefault());
            }

            return ratePlans;
        }

        private List<ResultRatePool> GetResultRatePools(KeySysLambdaContext context, OptimizationInstance instance, BillingPeriod billingPeriod, bool usesProration, List<long> queueIds, bool isCustomerOptimization)
        {
            LogInfo(context, LogTypeConstant.Sub, $"(,,,{string.Join(',', queueIds)})");
            var ratePlans = GetRatePlansByPortalType(context, instance, isCustomerOptimization, billingPeriod.Id);

            //process for cross pooled sims & adding split report by rate pools
            var planPoolMappings = GetRatePlanToRatePoolMappingByPortalType(context, queueIds, instance.PortalType);

            var ratePools = GenerateResultRatePoolFromRatePlans(billingPeriod, usesProration, ratePlans, planPoolMappings, false, instance);

            if (isCustomerOptimization)
            {
                var crossPooledPlans = GetCrossCustomerRatePlans(context, planPoolMappings.Select(mapping => mapping.RatePlanId).Distinct().ToList());
                //filter out duplicated rate plans
                crossPooledPlans = crossPooledPlans.Except(ratePlans).ToList();

                if (crossPooledPlans.Count > 0)
                {
                    ratePools.AddRange(GenerateResultRatePoolFromRatePlans(billingPeriod, usesProration, crossPooledPlans, planPoolMappings, true, instance));
                }
            }

            return ratePools;
        }

        private List<RatePlan> GetRatePlansByPortalType(KeySysLambdaContext context, OptimizationInstance instance, bool isCustomerOptimization, int billingPeriodId)
        {
            LogInfo(context, LogTypeConstant.Sub, $"instance.PortalType: {instance.PortalType}");
            var ratePlans = new List<RatePlan>();

            if (instance.PortalType == PortalTypes.Mobility)
            {
                ratePlans = GetMobilityRatePlans(context, instance, isCustomerOptimization, billingPeriodId);
            }
            else if (instance.PortalType == PortalTypes.M2M)
            {
                ratePlans = GetM2MRatePlans(context, instance, billingPeriodId);
            }
            else if (instance.PortalType == PortalTypes.CrossProvider)
            {
                var customerBillingPeriod = crossProviderOptimizationRepository.GetBillingPeriod(ParameterizedLog(context), instance.AMOPCustomerId.GetValueOrDefault(), instance.CustomerBillingPeriodId.GetValueOrDefault(), context.OptimizationSettings.BillingTimeZone);
                ratePlans = customerRatePlanRepository.GetCrossProviderCustomerRatePlans(ParameterizedLog(context), instance.ServiceProviderIds, instance.CustomerType, new List<int> { instance.AMOPCustomerId.GetValueOrDefault() }, customerBillingPeriod, instance.TenantId);
            }
            else
            {
                OptimizationErrorHandler.OnPortalTypeError(context, instance.PortalType, true);
            }

            return ratePlans;
        }

        private static List<ResultRatePool> GenerateResultRatePoolFromRatePlans(BillingPeriod billingPeriod, bool usesProration, List<RatePlan> ratePlans, List<RatePlanPoolMapping> planPoolMappings, bool isSharedRatePool, OptimizationInstance instance)
        {
            var ratePools = new List<ResultRatePool>();
            foreach (var ratePlan in ratePlans.OrderBy(ratePlan => ratePlan.PlanDisplayName))
            {
                var matchingMappings = planPoolMappings.Where(planPoolMapping => planPoolMapping.RatePlanId == ratePlan.Id);
                if (matchingMappings.Count() > 0)
                {
                    foreach (var matchingMapping in matchingMappings)
                    {
                        ratePools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, instance.RatePoolKeyType, matchingMapping.RatePoolName, isSharedRatePool));
                    }
                }
                else
                {
                    ratePools.Add(new ResultRatePool(ratePlan, usesProration, billingPeriod, instance.RatePoolKeyType, isSharedRatePool: isSharedRatePool));
                }
            }
            return ratePools;
        }

        private List<RatePlan> GetMobilityRatePlans(KeySysLambdaContext context, OptimizationInstance instance, bool isCustomerOptimization, int billingPeriodId)
        {
            List<RatePlan> tempRatePlans;
            if (!isCustomerOptimization)
            {
                return carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());
            }
            if (instance.RevCustomerId != null)
            {
                tempRatePlans = GetMobilityCustomerRatePlans(context, instance.RevCustomerId.Value, billingPeriodId, instance.ServiceProviderId, instance.TenantId, instance.CustomerType, instance.AMOPCustomerId);
            }
            else if (instance.AMOPCustomerId.HasValue)
            {
                tempRatePlans = GetMobilityCustomerRatePlans(context, Guid.Empty, billingPeriodId, instance.ServiceProviderId, instance.TenantId, instance.CustomerType, instance.AMOPCustomerId);
            }
            else
            {
                tempRatePlans = GetNonRetiredRatePlans(context, instance.ServiceProviderId.GetValueOrDefault());
            }

            return tempRatePlans;
        }

        public List<RatePlanPoolMapping> GetRatePlanToRatePoolMapping(KeySysLambdaContext context, List<long> queueIds, PortalTypes portalType)
        {
            try
            {
                LogInfo(context, LogTypeConstant.Sub, $"({string.Join(',', queueIds)}, {portalType})");
                var ratePlanPoolMappings = new List<RatePlanPoolMapping>();
                var resultTableName = GetResultTableNameFromPortalType(context, portalType);
                var sharedResultTableName = GetSharedResultTableNameFromPortalType(context, portalType);
                using (var conn = new SqlConnection(context.ConnectionString))
                {
                    conn.Open();

                    using (var cmd = new SqlCommand(GetRatePlanPoolMappingQueryString(resultTableName), conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.CommandTimeout = SQLConstant.ShortTimeoutSeconds;
                        cmd.Parameters.AddWithValue("@QueueIds", string.Join(',', queueIds));

                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            var ratePlanPoolMapping = RatePlanPoolMappingFromReader(rdr);
                            ratePlanPoolMappings.Add(ratePlanPoolMapping);
                        }
                    }

                    using (var cmd = new SqlCommand(GetRatePlanPoolMappingQueryString(sharedResultTableName), conn))
                    {
                        cmd.CommandType = CommandType.Text;
                        cmd.CommandTimeout = SQLConstant.ShortTimeoutSeconds;
                        cmd.Parameters.AddWithValue("@QueueIds", string.Join(',', queueIds));

                        var rdr = cmd.ExecuteReader();
                        while (rdr.Read())
                        {
                            var ratePlanPoolMapping = RatePlanPoolMappingFromReader(rdr);
                            if (!ratePlanPoolMappings.Any(existingMapping =>
                                                            existingMapping.RatePlanId == ratePlanPoolMapping.RatePlanId
                                                            && existingMapping.RatePoolId == ratePlanPoolMapping.RatePoolId))
                            {
                                ratePlanPoolMappings.Add(ratePlanPoolMapping);
                            }
                        }
                    }
                }

                return ratePlanPoolMappings;
            }
            catch (SqlException ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when executing stored procedure: {ex.Message}, ErrorCode:{ex.ErrorCode}-{ex.Number}, Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (InvalidOperationException ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when connecting to database: {ex.Message}, Stack Trace: {ex.StackTrace}");
                throw;
            }
            catch (Exception ex)
            {
                LogInfo(context, LogTypeConstant.Exception, $"Exception when getting rate plan to rate pool mapping for queue Ids {string.Join(',', queueIds)} : {ex.Message}, Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private static string GetRatePlanPoolMappingQueryString(string resultTableName)
        {
            return @$"SELECT ISNULL(carrierPlan.[RatePlanCode], customerPlan.[RatePlanCode]) AS RatePlanCode,
                            ISNULL(deviceResult.[AssignedCustomerRatePlanId], deviceResult.[AssignedCarrierRatePlanId]) AS RatePlanId, 
                            deviceResult.[CustomerRatePoolId] AS RatePoolId,
                            customerPool.[Name] AS RatePoolName
                    FROM {resultTableName} deviceResult 
                    LEFT JOIN JasperCarrierRatePlan carrierPlan ON deviceResult.[AssignedCarrierRatePlanId] = carrierPlan.[Id]
                    LEFT JOIN JasperCustomerRatePlan customerPlan ON deviceResult.[AssignedCustomerRatePlanId] = customerPlan.[Id] 
                    LEFT JOIN CustomerRatePool customerPool ON deviceResult.[CustomerRatePoolId] = customerPool.[Id]
                    WHERE QueueId in (SELECT CAST(value AS int) FROM STRING_SPLIT(@QueueIds,','))
                    GROUP BY carrierPlan.[RatePlanCode], 
                                customerPlan.[RatePlanCode],
                                deviceResult.[AssignedCustomerRatePlanId],
                                deviceResult.[AssignedCarrierRatePlanId],
                                deviceResult.[CustomerRatePoolId],
                                customerPool.[Name]";
        }

        private static string GetResultTableNameFromPortalType(KeySysLambdaContext context, PortalTypes portalType)
        {
            if (portalType == PortalTypes.Mobility)
            {
                return DatabaseTableNames.OptimizationMobilityDeviceResult;
            }
            else if (portalType == PortalTypes.M2M)
            {
                return DatabaseTableNames.OptimizationDeviceResult;
            }
            else
            {
                OptimizationErrorHandler.OnPortalTypeError(context, portalType, true);
                return string.Empty;
            }
        }

        private static string GetSharedResultTableNameFromPortalType(KeySysLambdaContext context, PortalTypes portalType)
        {
            if (portalType == PortalTypes.Mobility)
            {
                return DatabaseTableNames.OptimizationMobilitySharedPoolResult;
            }
            else if (portalType == PortalTypes.M2M)
            {
                return DatabaseTableNames.OptimizationSharedPoolResult;
            }
            else
            {
                OptimizationErrorHandler.OnPortalTypeError(context, portalType, true);
                return string.Empty;
            }
        }

        private MobilityOptimizationResult BuildMobilityOptimizationResult(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, MobilityOptimizationResult result, bool shouldSkipAutoChangeRatePlan = false)
        {
            AddSimCardsToResultRatePools(deviceResults, ratePools);
            var tempRPList = new List<ResultRatePool>(ratePools);
            if (shouldSkipAutoChangeRatePlan)
            {
                tempRPList = tempRPList.Where(ratePool => !ratePool.RatePlan.AutoChangeRatePlan).ToList();
            }
            var collection = new MobilityRatePoolCollection(tempRPList);
            result.RawRatePools = new List<MobilityRatePoolCollection>() { collection };
            result.CombinedRatePools = collection;
            return result;
        }

        private M2MOptimizationResult BuildM2MOptimizationResult(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, M2MOptimizationResult result, bool shouldSkipAutoChangeRatePlan = false)
        {
            AddSimCardsToResultRatePools(deviceResults, ratePools, shouldSkipAutoChangeRatePlan);
            var tempRPList = new List<ResultRatePool>(ratePools);
            if (shouldSkipAutoChangeRatePlan)
            {
                tempRPList = tempRPList.Where(ratePool => !ratePool.RatePlan.AutoChangeRatePlan).ToList();
            }
            var collection = new M2MRatePoolCollection(tempRPList);
            result.RawRatePools = new List<M2MRatePoolCollection>() { collection };
            result.CombinedRatePools = collection;
            return result;
        }

        private static void AddSimCardsToResultRatePools(List<SimCardResult> deviceResults, List<ResultRatePool> ratePools, bool shouldSkipAutoChangeRatePlan = false)
        {
            foreach (var deviceResult in deviceResults)
            {
                foreach (var pool in ratePools)
                {
                    var deviceKey = ResultRatePool.SimCardKeyByType(pool.KeyType, deviceResult);
                    if (pool.RatePlan.Id == deviceResult.RatePlanId && pool.RatePoolName == deviceResult.RatePoolName)
                    {
                        if (shouldSkipAutoChangeRatePlan && pool.RatePlan.AutoChangeRatePlan)
                        {
                            // skip the device even if it match
                            break;
                        }
                        if (pool.SimCards.ContainsKey(deviceKey))
                        {
                            pool.SimCards[deviceKey] = pool.SimCards[deviceKey].MergeSimCardResult(deviceResult);
                        }
                        else
                        {
                            pool.AddSimCard(deviceResult);
                        }

                        break;
                    }
                    // last rate plan would be the Unassigned plan
                    else if (pool.RatePlan.Id == OptimizationConstant.UnassignedRatePlanId)
                    {
                        if (pool.SimCards.ContainsKey(deviceKey))
                        {
                            pool.SimCards[deviceKey] = pool.SimCards[deviceKey].MergeSimCardResult(deviceResult);
                        }
                        else
                        {
                            pool.AddSimCard(deviceResult);
                        }

                        break;
                    }
                }
            }
        }

        protected virtual OptimizationInstanceResultFile SaveOptimizationInstanceResultFile(KeySysLambdaContext context, long instanceId, byte[] assignmentXlsxBytes, int totalDeviceCount = 0)
        {
            LogInfo(context, "SUB", $"SaveOptimizationInstanceResultFile({instanceId})");
            var resultFile = new OptimizationInstanceResultFile()
            {
                InstanceId = instanceId,
                AssignmentXlsxBytes = assignmentXlsxBytes,
                TotalDeviceCount = totalDeviceCount
            };

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("INSERT INTO OptimizationInstanceResultFile(InstanceId, AssignmentXlsxBytes, CreatedBy, CreatedDate, IsDeleted) VALUES(@instanceId, @assignmentXlsxBytes, 'System', GETUTCDATE(), 0)", conn))
                {
                    cmd.CommandTimeout = 180;
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@instanceId", resultFile.InstanceId);
                    cmd.Parameters.AddWithValue("@assignmentXlsxBytes", resultFile.AssignmentXlsxBytes);
                    conn.Open();

                    cmd.ExecuteNonQuery();

                    conn.Close();
                }

                using (var cmd = new SqlCommand("SELECT TOP 1 Id FROM OptimizationInstanceResultFile WHERE InstanceId = @instanceId", conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@instanceId", resultFile.InstanceId);
                    conn.Open();

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            resultFile.Id = long.Parse(reader[0].ToString());
                        }
                    }

                    conn.Close();
                }
            }

            return resultFile;
        }

        private RevCustomer GetRevCustomerById(KeySysLambdaContext context, Guid customerGuidId)
        {
            var customer = new RevCustomer
            {
                id = customerGuidId
            };

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandText = "dbo.usp_RevCustomer_GetById";
                    cmd.Parameters.AddWithValue("@ID", customerGuidId);
                    conn.Open();

                    SqlDataReader rdr = cmd.ExecuteReader();
                    rdr.Read();

                    if (!rdr.IsDBNull(rdr.GetOrdinal("RevCustomerId")))
                    {
                        customer.RevCustomerId = rdr["RevCustomerId"].ToString();
                        customer.CustomerName = rdr["CustomerName"].ToString();
                    }

                    conn.Close();
                }
            }

            return customer;
        }

        private int? GetTotalSimCountForCustomer(KeySysLambdaContext context, string revAccountNumber, int tenantId)
        {
            int? result;

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandText = "dbo.usp_Device_GetTotalSimCountByAccountNumber";
                    cmd.Parameters.AddWithValue("@RevCustomerNumbers", revAccountNumber);
                    cmd.Parameters.AddWithValue("@TenantId", tenantId);
                    conn.Open();

                    result = (Int32)cmd.ExecuteScalar();

                    conn.Close();
                }
            }

            return result;
        }

        private int? GetTotalMobilitySimCountForCustomer(KeySysLambdaContext context, string revAccountNumber, int tenantId)
        {
            int? result;

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandText = "dbo.usp_MobilityDevice_GetTotalSimCountByAccountNumber";
                    cmd.Parameters.AddWithValue("@RevCustomerNumber", revAccountNumber);
                    cmd.Parameters.AddWithValue("@TenantId", tenantId);
                    conn.Open();

                    result = (Int32)cmd.ExecuteScalar();

                    conn.Close();
                }
            }

            return result;
        }

        private Site GetAMOPCustomerById(KeySysLambdaContext context, int amopCustomerId)
        {
            LogInfo(context, "SUB", $"GetAMOPCustomerById(,{amopCustomerId})");
            var customer = new Site
            {
                Id = amopCustomerId
            };

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("SELECT TOP 1 id, Name FROM [Site] WHERE id = @amopCustomerId", conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@amopCustomerId", amopCustomerId);
                    conn.Open();

                    var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        customer.Id = int.Parse(rdr["id"].ToString());
                        customer.Name = rdr["Name"].ToString();
                    }

                    conn.Close();
                }
            }

            return customer;
        }

        private int? GetTotalSimCountForAMOPCustomerId(KeySysLambdaContext context, int amopCustomerId, int tenantId)
        {
            int? result;

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandText = "dbo.usp_Device_GetTotalSimCountBySiteId";
                    cmd.Parameters.AddWithValue("@SiteIds", amopCustomerId.ToString());
                    cmd.Parameters.AddWithValue("@TenantId", tenantId);
                    conn.Open();

                    result = (Int32)cmd.ExecuteScalar();

                    conn.Close();
                }
            }

            return result;
        }

        private int? GetTotalMobilitySimCountForAMOPCustomerId(KeySysLambdaContext context, int amopCustomerId, int tenantId)
        {
            int? result;

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandText = "dbo.usp_MobilityDevice_GetTotalSimCountBySiteId";
                    cmd.Parameters.AddWithValue("@SiteIds", amopCustomerId.ToString());
                    cmd.Parameters.AddWithValue("@TenantId", tenantId);
                    conn.Open();

                    result = (Int32)cmd.ExecuteScalar();

                    conn.Close();
                }
            }

            return result;
        }

        private void SendGoForRatePlanUpdatesEmail(KeySysLambdaContext context, OptimizationInstance instance, TimeZoneInfo billingTimeZone)
        {
            var subject = context.OptimizationSettings.GoForRatePlanUpdateEmailSubject;
            var fromEmailAddress = context.OptimizationSettings.FromEmailAddress;
            var recipientAddressList = context.OptimizationSettings.ToEmailAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var bccAddressList = context.OptimizationSettings.BccEmailAddresses?.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var body = BuildGoForRatePlanUpdateEmailBody(instance, billingTimeZone);

            SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);
        }

        private void SendNoGoForRatePlanUpdatesEmail(KeySysLambdaContext context, OptimizationInstance instance, TimeZoneInfo billingTimeZone)
        {
            var subject = context.OptimizationSettings.NoGoForRatePlanUpdateEmailSubject;
            var fromEmailAddress = context.OptimizationSettings.FromEmailAddress;
            var recipientAddressList = context.OptimizationSettings.ToEmailAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var bccAddressList = context.OptimizationSettings.BccEmailAddresses?.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var body = BuildNoGoForRatePlanUpdateEmailBody(instance, billingTimeZone);

            SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);
        }

        private BodyBuilder BuildGoForRatePlanUpdateEmailBody(OptimizationInstance instance, TimeZoneInfo billingTimeZone)
        {
            var runStartTime = (instance.RunEndTime != null
                ? TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime.Value, billingTimeZone)
                    .ToString(CultureInfo.InvariantCulture)
                : "");
            var body = new BodyBuilder()
            {
                HtmlBody = $"<div>AMOP is automatically starting Rate Plan Updates for the Carrier Optimization started on: {runStartTime}.</div>",
                TextBody = $"AMOP is automatically starting Rate Plan Updates for the Carrier Optimization started on: {runStartTime}."
            };

            return body;
        }

        private BodyBuilder BuildNoGoForRatePlanUpdateEmailBody(OptimizationInstance instance, TimeZoneInfo billingTimeZone)
        {
            var runStartTime = (instance.RunEndTime != null
                ? TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime.Value, billingTimeZone)
                    .ToString(CultureInfo.InvariantCulture)
                : "");
            var body = new BodyBuilder()
            {
                HtmlBody = $"<div>AMOP has determined that Rate Plan Updates cannot finish for the Carrier Optimization started on: {runStartTime} before the billing period ends.</div><br/><div>To prevent issues with a partial update, the system will not proceed.</div>",
                TextBody = $"AMOP has determined that Rate Plan Updates cannot finish for the Carrier Optimization started on: {runStartTime} before the billing period ends.{Environment.NewLine}To prevent issues with a partial update, the system will not proceed."
            };

            return body;
        }

        private void SendResults(KeySysLambdaContext context, OptimizationInstance instance, byte[] assignmentXlsxBytes, TimeZoneInfo billingTimeZone,
            DeviceSyncSummary syncResults, IntegrationType integrationType, IList<IntegrationTypeModel> integrationTypes)
        {
            LogInfo(context, "SUB", $"SendResults({instance.Id})");

            string subject;
            string fromEmailAddress;
            List<string> recipientAddressList;
            var bccAddressList = context.OptimizationSettings.BccEmailAddresses?.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (instance.RevCustomerId == null && !instance.AMOPCustomerId.HasValue)
            {
                //Format email for Carrier Optimization
                fromEmailAddress = context.OptimizationSettings.FromEmailAddress;
                recipientAddressList = context.OptimizationSettings.ToEmailAddresses
                    .Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                subject = context.OptimizationSettings.ResultsEmailSubject;
            }
            else
            {
                //Format email for Customer Optimization
                fromEmailAddress = context.OptimizationSettings.CustomerFromEmailAddress;
                recipientAddressList = context.OptimizationSettings.CustomerToEmailAddresses
                    .Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (instance.AMOPCustomerId.HasValue)// Non-Rev
                {
                    var amopCustomer = GetAMOPCustomerById(context, instance.AMOPCustomerId.Value);
                    LogInfo(context, "SUB", $"SendResults(Email to Customer - Account: {amopCustomer.Id}, Name: {amopCustomer.Name} )");

                    var totalM2MSimCount = GetTotalSimCountForAMOPCustomerId(context, instance.AMOPCustomerId.Value, instance.TenantId);
                    var totalMobilitySimCount = GetTotalMobilitySimCountForAMOPCustomerId(context, instance.AMOPCustomerId.Value, instance.TenantId);
                    syncResults.DeviceCount = totalM2MSimCount.GetValueOrDefault() + totalMobilitySimCount.GetValueOrDefault();

                    subject = string.Format(context.OptimizationSettings.ResultsCustomerEmailSubject, amopCustomer.Name);
                }
                else
                {
                    var customer = GetRevCustomerById(context, instance.RevCustomerId.Value);
                    LogInfo(context, "SUB", $"SendResults(Email to Customer - Account: {customer.RevCustomerId}, Name: {customer.DisplayName} )");

                    var totalM2MSimCount = GetTotalSimCountForCustomer(context, customer.RevCustomerId, instance.TenantId);
                    var totalMobilitySimCount = GetTotalMobilitySimCountForCustomer(context, customer.RevCustomerId, instance.TenantId);
                    syncResults.DeviceCount = totalM2MSimCount.GetValueOrDefault() + totalMobilitySimCount.GetValueOrDefault();

                    subject = string.Format(context.OptimizationSettings.ResultsCustomerEmailSubject, customer.DisplayName);
                }

            }
            var integrationName = integrationTypes.FirstOrDefault(it => it.Id == (int)integrationType)?.Name;

            subject = subject.Replace("Jasper",
                !string.IsNullOrEmpty(integrationName) ? integrationName : integrationType.ToString("G"));
            var body = BuildResultsEmailBody(context, instance, assignmentXlsxBytes, billingTimeZone, syncResults);

            SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);
        }

        private void OptimizationCustomerSendResults(KeySysLambdaContext context, OptimizationInstance instance, DeviceSyncSummary syncResults, bool isLastInstance, int serviceProviderId)
        {
            LogInfo(context, "SUB", $"OptimizationCustomerSendResults()");

            if (instance.AMOPCustomerId.HasValue)// Non-Rev
            {
                var amopCustomer = GetAMOPCustomerById(context, instance.AMOPCustomerId.Value);
                LogInfo(context, "SUB", $"SendResults(Email to Customer - Account: {amopCustomer.Id}, Name: {amopCustomer.Name} )");

                var totalM2MSimCount = GetTotalSimCountForAMOPCustomerId(context, instance.AMOPCustomerId.Value, instance.TenantId);
                var totalMobilitySimCount = GetTotalMobilitySimCountForAMOPCustomerId(context, instance.AMOPCustomerId.Value, instance.TenantId);
                syncResults.DeviceCount = totalM2MSimCount.GetValueOrDefault() + totalMobilitySimCount.GetValueOrDefault();

                // update OptimizationCustomerProcessing
                UpdateOptCustomerProcessing(context, amopCustomer.Id.ToString(), DateTime.UtcNow, (int)syncResults.DeviceCount, serviceProviderId, SiteTypes.AMOP, instance);
            }
            else
            {
                var customer = GetRevCustomerById(context, instance.RevCustomerId.Value);
                LogInfo(context, "SUB", $"SendResults(Email to Customer - Account: {customer.RevCustomerId}, Name: {customer.DisplayName} )");

                var totalM2MSimCount = GetTotalSimCountForCustomer(context, customer.RevCustomerId, instance.TenantId);
                var totalMobilitySimCount = GetTotalMobilitySimCountForCustomer(context, customer.RevCustomerId, instance.TenantId);
                syncResults.DeviceCount = totalM2MSimCount.GetValueOrDefault() + totalMobilitySimCount.GetValueOrDefault();

                // update OptimizationCustomerProcessing
                UpdateOptCustomerProcessing(context, customer.RevCustomerId, DateTime.UtcNow, (int)syncResults.DeviceCount, serviceProviderId, SiteTypes.Rev, instance);
            }

            if (isLastInstance)
            {
                // send message Cleanup to Send Optimization Email
                QueueLastStepOptCustomerCleanup(context, instance.Id, instance.SessionId.Value, true, serviceProviderId, _optCustomerCleanUpDelaySeconds);
            }
        }

        private void UpdateOptCustomerProcessing(KeySysLambdaContext context, string customerId, DateTime endTime, int deviceCount, int serviceProviderId, SiteTypes siteType, OptimizationInstance instance)
        {
            LogInfo(context, "SUB", $"UpdateOptCustomerProcessing({customerId}, {endTime}, {deviceCount})");

            var query = @"UPDATE [OptimizationCustomerProcessing]
                            SET [DeviceCount] = @deviceCount,
                                [IsProcessed] = @isProcessing,
                                [EndTime] = @endTime,
                                [InstanceId] = @instanceId
                            WHERE {0}
                            AND [ServiceProviderId] = @serviceProviderId
                            AND [SessionId] = @sessionId";

            if (siteType == SiteTypes.Rev)
            {
                query = string.Format(query, "[CustomerId] = @customerId");
            }
            else
            {
                query = string.Format(query, "[AMOPCustomerId] = @amopCustomerId");
            }

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@deviceCount", deviceCount);
                    cmd.Parameters.AddWithValue("@isProcessing", true);
                    cmd.Parameters.AddWithValue("@endTime", endTime);
                    cmd.Parameters.AddWithValue("@serviceProviderId", serviceProviderId);
                    cmd.Parameters.AddWithValue("@instanceId", instance.Id);
                    cmd.Parameters.AddWithValue("@sessionId", instance.SessionId);

                    if (siteType == SiteTypes.Rev)
                    {
                        cmd.Parameters.AddWithValue("@customerId", customerId);
                    }
                    else
                    {
                        cmd.Parameters.AddWithValue("@amopCustomerId", customerId);
                    }

                    conn.Open();

                    var rdr = cmd.ExecuteNonQuery();
                    conn.Close();
                }
            }
        }

        private void DeleteDataFromOptCustomerProcessing(KeySysLambdaContext context, int serviceProviderId, long sessionId)
        {
            LogInfo(context, "SUB", $"DeleteDataFromOptCustomerProcessing({serviceProviderId})");

            string query;
            if (serviceProviderId > 0)
            {
                query = @"DELETE FROM [OptimizationCustomerProcessing]
                            WHERE [ServiceProviderId] = @serviceProviderId AND [SessionId] = @sessionId";
            }
            else
            {
                query = @"DELETE FROM [OptimizationCustomerProcessing]
                            WHERE [SessionId] = @sessionId";
            }
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandType = CommandType.Text;
                    if (serviceProviderId > 0)
                    {
                        cmd.Parameters.AddWithValue("@serviceProviderId", serviceProviderId);
                    }
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    conn.Open();

                    var rdr = cmd.ExecuteNonQuery();
                    conn.Close();
                }
            }
        }

        private bool CheckOptCustomerProcessing(KeySysLambdaContext context, int serviceProviderId, long sessionId)
        {
            LogInfo(context, "SUB", $"CheckOptCustomerProcess({serviceProviderId})");

            int record = 0;
            string query;
            if (serviceProviderId > 0)
            {
                query = @"SELECT COUNT(*) FROM [OptimizationCustomerProcessing]
                            WHERE [ServiceProviderId] = @serviceProviderId AND [IsProcessed] = @isProcessed AND [SessionId] = @sessionId";
            }
            else
            {
                query = @"SELECT COUNT(*) FROM [OptimizationCustomerProcessing]
                            WHERE [IsProcessed] = @isProcessed AND [SessionId] = @sessionId";
            }
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@isProcessed", false);
                    if (serviceProviderId > 0)
                    {
                        cmd.Parameters.AddWithValue("@serviceProviderId", serviceProviderId);
                    }
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    conn.Open();

                    var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        record = int.Parse(rdr[0].ToString());
                    }

                    conn.Close();
                }
            }
            return record > 0 ? true : false;
        }

        private List<OptimizationCustomerProcessing> GetOptCustomerProcessing(KeySysLambdaContext context, int serviceProviderId, long sessionId)
        {
            LogInfo(context, "SUB", $"CheckOptCustomerProcess({serviceProviderId})");

            var result = new List<OptimizationCustomerProcessing>();
            var query = @"SELECT [ServiceProviderId], [CustomerId], [CustomerName], [DeviceCount], [IsProcessed], [StartTime], [EndTime], s.[Name], o.[AMOPCustomerId], o.[AMOPCustomerName]
                          FROM [OptimizationCustomerProcessing] o
                          JOIN ServiceProvider s ON s.Id = o.ServiceProviderId
                          WHERE [ServiceProviderId] = @serviceProviderId AND [IsProcessed] = @isProcessed AND [SessionId] = @sessionId";
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@isProcessed", true);
                    cmd.Parameters.AddWithValue("@serviceProviderId", serviceProviderId);
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    conn.Open();

                    var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var opt = new OptimizationCustomerProcessing()
                        {
                            ServiceProviderId = int.Parse(rdr[0].ToString()),
                            CustomerId = rdr[1].ToString(),
                            CustomerName = rdr[2].ToString(),
                            DeviceCount = int.Parse(rdr[3].ToString()),
                            IsProcessed = Convert.ToBoolean(rdr[4].ToString()),
                            StartTime = DateTime.Parse(rdr[5].ToString()),
                            EndTime = DateTime.Parse(rdr[6].ToString()),
                            ServiceProviderName = rdr[7].ToString(),
                            AMOPCustomerId = rdr[8] != DBNull.Value ? int.Parse(rdr[8].ToString()) : 0,
                            AMOPCustomerName = rdr[9].ToString(),
                        };
                        result.Add(opt);
                    }

                    conn.Close();
                }
            }
            return result;
        }

        private void SendOptimizationEmail(KeySysLambdaContext context, string subject, BodyBuilder body,
            string fromEmailAddress, List<string> recipientAddressList, List<string> bccAddressList)
        {
            LogInfo(context, "SUB", $"SendOptimizationEmail({subject})");
            var credentials = context.GeneralProviderSettings.AwsSesCredentials;
            using (var client = new AmazonSimpleEmailServiceClient(credentials, RegionEndpoint.USEast1))
            {
                var message = new MimeMessage();
                message.From.Add(MailboxAddress.Parse(fromEmailAddress));
                message.Subject = subject;

                foreach (var recipientAddress in recipientAddressList)
                {
                    message.To.Add(MailboxAddress.Parse(recipientAddress));
                }

                foreach (var bccAddress in bccAddressList)
                {
                    message.Bcc.Add(MailboxAddress.Parse(bccAddress));
                }

                message.Body = body.ToMessageBody();
                var stream = new System.IO.MemoryStream();
                message.WriteTo(stream);

                var sendRequest = new SendRawEmailRequest()
                {
                    RawMessage = new RawMessage(stream)
                };
                try
                {
                    var response = client.SendRawEmailAsync(sendRequest).Result;
                }
                catch (Exception ex)
                {
                    LogInfo(context, "Error Sending Optimization Email", ex.Message);
                }
            }
        }

        private BodyBuilder BuildResultsEmailBody(KeySysLambdaContext context, OptimizationInstance instance, byte[] assignmentXlsxBytes, TimeZoneInfo billingTimeZone,
            DeviceSyncSummary syncResults)
        {
            LogInfo(context, "SUB", $"BuildResultsEmailBody({instance.Id})");

            var runStartTime = (instance.RunStartTime != null
                ? TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime.Value, billingTimeZone)
                    .ToString(CultureInfo.InvariantCulture)
                : "");
            var runEndTime = (instance.RunEndTime != null
                ? TimeZoneInfo.ConvertTimeFromUtc(instance.RunEndTime.Value, billingTimeZone)
                    .ToString(CultureInfo.InvariantCulture)
                : "");
            var deviceDetailSyncDate = (syncResults.DetailLastSyncDate != null ? syncResults.DetailLastSyncDate.Value.ToString(CultureInfo.InvariantCulture) : "");
            var deviceUsageSyncDate = (syncResults.UsageLastSyncDate != null ? syncResults.UsageLastSyncDate.Value.ToString(CultureInfo.InvariantCulture) : "");
            var simCount = (syncResults.DeviceCount != null ? syncResults.DeviceCount.Value.ToString() : "");
            var body = new BodyBuilder()
            {
                HtmlBody = $"<div>Here are your optimization Results for Billing Period Ending on {instance.BillingPeriodEndDate.ToShortDateString()} {instance.BillingPeriodEndDate.ToShortTimeString()}. Optimization started on: {runStartTime}. Optimization completed on: {runEndTime}.</div><br/><div>Last Device Detail Sync Date: {deviceDetailSyncDate}<br/>Last Device Usage Sync Date: {deviceUsageSyncDate}<br/>Total SIM Cards: {simCount}<br/>Execution OU: {context.OptimizationSettings.ExecutionOU}</div>",
                TextBody = $"Optimization Results for Billing Period Ending on {instance.BillingPeriodEndDate.ToShortDateString()} {instance.BillingPeriodEndDate.ToShortTimeString()}. Optimization started on: {runStartTime}. Optimization completed on: {runEndTime}.{Environment.NewLine}Last Device Detail Sync Date: {deviceDetailSyncDate}{Environment.NewLine}Last Device Usage Sync Date: {deviceUsageSyncDate}{Environment.NewLine}Total SIM Cards: {simCount}{Environment.NewLine}Execution OU: {context.OptimizationSettings.ExecutionOU}"
            };
            body.Attachments.Add("device_assignments.xlsx", assignmentXlsxBytes, new ContentType("application", "vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

            return body;
        }

        private string OptCustomerResultsBody(KeySysLambdaContext context, OptimizationInstance instance,
            List<OptimizationCustomerProcessing> optCustomerProcessing, string runStartTime, string runEndTime, string deviceDetailSyncDate, string deviceUsageSyncDate, string simCount)
        {
            var stringBuilder = new StringBuilder($@"
                <html>
                <head>
                <style>
                body {{
                    background-color: #fff;
                    font-family: ""Lato"", ""Helvetica Neue"", Helvetica, Arial, sans-serif;
                    z-index: 0;
                    position: relative;
                    top: 0;
                    left: 0;
                    top: 0;
                    bottom: 0;
                }}

                tr {{
                    text-align: left;
                }}

                th,td {{
                    padding-right: 10px;
                }}

                </style>
                </head>

                <div>
                    Here are your optimization Results for Billing Period Ending on {instance.BillingPeriodEndDate.ToShortDateString()} {instance.BillingPeriodEndDate.ToShortTimeString()}. 
                    Optimization started on: {runStartTime}. Optimization completed on: {runEndTime}.</div><br/>
                    <div>Last Device Detail Sync Date: {deviceDetailSyncDate}
                    <br/>Last Device Usage Sync Date: {deviceUsageSyncDate}
                    <br/>Total SIM Cards: {simCount}
                    <br/>Execution OU: {context.OptimizationSettings.ExecutionOU}
                </div>
                <br/>
                <table>
                <tr><th>No.</th><th>Customer Name</th></tr>");

            foreach (var opt in optCustomerProcessing.Select((item, index) => new { item, index }))
            {
                var customerName = opt.item.CustomerName;
                if (instance.CustomerType == SiteTypes.AMOP)
                {
                    customerName = opt.item.AMOPCustomerName;
                }
                stringBuilder.Append(
                    $"<tr><td>{opt.index + 1}</td><td>{customerName}</td></tr>");
            }

            stringBuilder.Append("</table>");
            stringBuilder.Append("</html>");
            return stringBuilder.ToString();
        }

        protected long GetWinningQueueId(KeySysLambdaContext context, long commGroupId)
        {
            LogInfo(context, "SUB", $"GetWinningQueueId({commGroupId})");
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("SELECT TOP 1 Id FROM OptimizationQueue WHERE CommPlanGroupId = @commGroupId AND TotalCost IS NOT NULL AND RunEndTime IS NOT NULL ORDER BY TotalCost ASC", conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@commGroupId", commGroupId);
                    conn.Open();

                    var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        return long.Parse(rdr["Id"].ToString());
                    }

                    conn.Close();
                }
            }

            return 0;
        }

        private void EndQueuesForCommGroup(KeySysLambdaContext context, long commGroupId)
        {
            LogInfo(context, "SUB", $"EndQueuesForCommGroup({commGroupId})");
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("UPDATE OptimizationQueue WITH (HOLDLOCK) SET RunEndTime = GETUTCDATE(), RunStatusId = @runStatusId, TotalCost = NULL WHERE CommPlanGroupId = @commGroupId AND RunEndTime IS NULL", conn))
                {
                    cmd.CommandTimeout = 900;
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@commGroupId", commGroupId);
                    cmd.Parameters.AddWithValue("@runStatusId", (int)OptimizationStatus.CompleteWithErrors);
                    conn.Open();

                    cmd.ExecuteNonQuery();

                    conn.Close();
                }
            }
        }

        private void CleanupDeviceResultsForCommGroup(KeySysLambdaContext context, long commGroupId, long queueId)
        {
            LogInfo(context, "SUB", $"CleanupDeviceResultsForCommGroup(CommGroupID:{commGroupId}, Winning Queue ID:{queueId})");

            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("usp_Optimization_DeviceResultAndQueueRatePlan_Cleanup", conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@commGroupId", commGroupId);
                    cmd.Parameters.AddWithValue("@winningQueueId", queueId);
                    cmd.CommandTimeout = 900;

                    conn.Open();

                    cmd.ExecuteNonQuery();

                    conn.Close();
                }
            }
        }

        private void QueueRatePlanUpdates(KeySysLambdaContext context, long instanceId, int tenantId)
        {
            LogInfo(context, "SUB", $"QueueRatePlanUpdates({instanceId})");

            var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var requestMsgBody = $"Rate Plan Update for Instance {instanceId}";
                var request = new SendMessageRequest
                {
                    DelaySeconds = (int)TimeSpan.FromSeconds(5).TotalSeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "InstanceId", new MessageAttributeValue
                            { DataType = "String", StringValue = instanceId.ToString()}
                        },
                        {
                            "TenantId", new MessageAttributeValue
                            { DataType = "String", StringValue = tenantId.ToString()}
                        }
                    },
                    MessageBody = requestMsgBody,
                    QueueUrl = context.QueueDestinationQueueUrl
                };

                var response = client.SendMessageAsync(request);
                response.Wait();
                if (response.Status == TaskStatus.Faulted || response.Status == TaskStatus.Canceled)
                {
                    LogInfo(context, "RESPONSE STATUS", $"Error Queuing Rate Plan Changes for {instanceId}: {response.Status}");
                }
            }
        }

        private void QueueLastStepOptCustomerCleanup(KeySysLambdaContext context, long instanceId, long sessionId, bool isOptLastStepSendEmail, int serviceProviderId, int delaySeconds = 0, int retryCount = 1)
        {
            LogInfo(context, "SUB", $"QueueLastStepOptCustomerCleanup({serviceProviderId})");
            LogInfo(context, "SUB", $"isOptLastStepSendEmail: ({isOptLastStepSendEmail})");
            LogInfo(context, "SUB", $"Queue url: ({context.QueueDestinationQueueUrl})");

            var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var requestMsgBody = $"Optimization Customer Send Email";
                var request = new SendMessageRequest
                {
                    DelaySeconds = delaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "InstanceId", new MessageAttributeValue
                            { DataType = "String", StringValue = instanceId.ToString()}
                        },
                        {
                            "SessionId", new MessageAttributeValue
                            { DataType = "String", StringValue = sessionId.ToString()}
                        },
                        {
                            "IsOptLastStepSendEmail", new MessageAttributeValue
                            { DataType = "String", StringValue = isOptLastStepSendEmail.ToString()}
                        },
                        {
                            "ServiceProviderId", new MessageAttributeValue
                            { DataType = "String", StringValue = serviceProviderId.ToString()}
                        },
                        {
                            "RetryCount", new MessageAttributeValue
                            { DataType = "String", StringValue = retryCount.ToString()}
                        },
                    },
                    MessageBody = requestMsgBody,
                    QueueUrl = context.CleanupDestinationQueueUrl
                };

                var response = client.SendMessageAsync(request);
                response.Wait();
                if (response.Status == TaskStatus.Faulted || response.Status == TaskStatus.Canceled)
                {
                    LogInfo(context, "RESPONSE STATUS", $"Error Optimization Customer Send Email: {response.Status}");
                }
            }
        }

        private void RequeueCleanup(KeySysLambdaContext context, long instanceId, int retryCount, int optimizationQueueLength, bool isCustomerOptimization)
        {
            LogInfo(context, "SUB", $"RequeueCleanup({instanceId},{retryCount},{optimizationQueueLength})");

            var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                retryCount += 1;
                int delaySeconds = DelaySecondsFromQueueLength(optimizationQueueLength);
                var requestMsgBody = $"Requeue Cleanup for Instance {instanceId}, Retry #{retryCount}";
                var request = new SendMessageRequest
                {
                    DelaySeconds = delaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "InstanceId", new MessageAttributeValue
                            { DataType = "String", StringValue = instanceId.ToString()}
                        },
                        {
                            "RetryCount", new MessageAttributeValue
                            { DataType = "String", StringValue = retryCount.ToString()}
                        },
                        {
                            "IsCustomerOptimization", new MessageAttributeValue
                            { DataType = "String", StringValue = isCustomerOptimization.ToString()}
                        }
                    },
                    MessageBody = requestMsgBody,
                    QueueUrl = context.CleanupDestinationQueueUrl
                };

                var response = client.SendMessageAsync(request);
                response.Wait();
                if (response.Status == TaskStatus.Faulted || response.Status == TaskStatus.Canceled)
                {
                    LogInfo(context, "RESPONSE STATUS", $"Error Requeuing Cleanup for {instanceId}: {response.Status}");
                }
            }
        }

        private int DelaySecondsFromQueueLength(int optimizationQueueLength)
        {
            // default delay per check
            var delaySeconds = 600;

            // if there are unstarted items in the queue, wait for those to at least start
            if (optimizationQueueLength > 50)
            {
                // can't delay more than 15 minutes in SQS
                delaySeconds = 900;
            }

            return delaySeconds;
        }

        protected OptimizationInstanceResultFile WriteCrossProviderCustomerResults(KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, bool usesProration)
        {
            LogInfo(context, CommonConstants.SUB, $"(,{instance.Id},{string.Join(',', queueIds)})");
            var isCustomerOptimization = true;
            var totalDeviceCount = 0;
            var customerBillingPeriod = crossProviderOptimizationRepository.GetBillingPeriod(ParameterizedLog(context), instance.AMOPCustomerId.GetValueOrDefault(), instance.CustomerBillingPeriodId.GetValueOrDefault(), context.OptimizationSettings.BillingTimeZone);
            // Reuse M2M optimization result model since there are no implementation difference with existing Single Provider M2M Customer Optimization
            var result = new M2MOptimizationResult();
            var crossCustomerResult = new M2MOptimizationResult();

            // Get rate pools
            var crossOptimizationResultRatePools = GetResultRatePools(context, instance, customerBillingPeriod, usesProration, queueIds, isCustomerOptimization);

            // Create another set of rate pools
            var optimizationResultRatePools = GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools);

            AddUnassignedRatePool(context, instance, customerBillingPeriod, usesProration, crossOptimizationResultRatePools, optimizationResultRatePools);

            foreach (var queueId in queueIds)
            {
                LogInfo(context, CommonConstants.INFO, $"Building results for Optimization Queue with Id: {queueId}.");
                // get results for queue id
                var deviceResults = crossProviderOptimizationRepository.GetCrossProviderResults(ParameterizedLog(context), new List<long>() { queueId }, customerBillingPeriod);
                totalDeviceCount += deviceResults.Count;
                // build optimization result
                result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
                var sharedPoolDeviceResults = crossProviderOptimizationRepository.GetCrossProviderSharedPoolResults(ParameterizedLog(context), new List<long>() { queueId }, customerBillingPeriod);
                sharedPoolDeviceResults.AddRange(deviceResults);
                crossCustomerResult = BuildM2MOptimizationResult(sharedPoolDeviceResults, crossOptimizationResultRatePools, crossCustomerResult, true);
            }

            // write result to stat file
            var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, result);

            // write result to device output file (text)
            var assignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(result);
            byte[] sharedPoolStatFileBytes = null;
            byte[] sharedPoolAssignmentFileBytes = null;

            if (crossCustomerResult.CombinedRatePools.TotalSimCardCount > result.CombinedRatePools.TotalSimCardCount)
            {
                // write shared pool result to stat file
                sharedPoolStatFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(SimCardGrouping.GroupByCommunicationPlan, crossCustomerResult);

                // write shared pool result to device output file (text)
                sharedPoolAssignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(crossCustomerResult);
            }

            // write result to device output file (xlsx)
            LogInfo(context, CommonConstants.SUB, $"({result.QueueId})");
            var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);

            // save to database
            return SaveOptimizationInstanceResultFile(context, instance.Id, assignmentXlsxBytes, totalDeviceCount);
        }

        public List<RatePlanPoolMapping> GetRatePlanToRatePoolMappingByPortalType(KeySysLambdaContext context, List<long> queueIds, PortalTypes portalType)
        {
            if (portalType == PortalTypes.CrossProvider)
            {
                var mappings = GetRatePlanToRatePoolMapping(context, queueIds, PortalTypes.M2M);
                mappings.AddRange(GetRatePlanToRatePoolMapping(context, queueIds, PortalTypes.Mobility));
                return mappings;
            }
            else
            {
                return GetRatePlanToRatePoolMapping(context, queueIds, portalType);
            }
        }

        private void ProcessResultForCrossProvider(KeySysLambdaContext context, bool isCustomerOptimization, bool isLastInstance, OptimizationInstance instance, OptimizationInstanceResultFile fileResult)
        {
            if (isCustomerOptimization)
            {
                var customer = GetRevCustomerById(context, instance.RevCustomerId.Value);
                crossProviderOptimizationRepository.UpdateProcessingCustomerOptimizationInstance(ParameterizedLog(context), instance.SessionId.GetValueOrDefault(), instance.Id, null, fileResult.TotalDeviceCount, false, instance.CustomerType, customer.RevCustomerId, instance.AMOPCustomerId);
                if (isLastInstance)
                {
                    // send message Cleanup to Send Optimization Email
                    QueueLastStepOptCustomerCleanup(context, instance.Id, instance.SessionId.Value, true, 0, _optCustomerCleanUpDelaySeconds);
                }
            }
        }
    }
}
