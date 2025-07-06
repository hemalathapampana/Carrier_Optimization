using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Altaworx.SimCard.Cost.Optimizer.Core;
using Altaworx.SimCard.Cost.Optimizer.Core.Enumerations;
using Altaworx.SimCard.Cost.Optimizer.Core.Factories;
using Altaworx.SimCard.Cost.Optimizer.Core.Helpers;
using Altaworx.SimCard.Cost.Optimizer.Core.Models;
using Altaworx.SimCard.Cost.Optimizer.Core.Repositories.ServiceProvider;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Runtime;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amop.Core.Logger;
using MimeKit;
using Amop.Core.Repositories.Jasper;
using Amop.Core.Models;
using Amop.Core.Constants;
using Altaworx.SimCard.Cost.Optimizer.Core.Repositories.CarrierRatePlan;
using Altaworx.SimCard.Cost.Optimizer.Core.Repositories.Optimization;
using Amop.Core.Helpers;
using Altaworx.AWS.Core.Services.SQS;
using System.Text.Json.Nodes;
using System.Text.Json;
using Amop.Core.Repositories.Environment;
using System.Net.Http;
using Amop.Core.Helpers.Pond;
using System.Text;
using Amazon.S3.Model;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace Altaworx.SimCard.Cost.QueueCarrierPlanOptimization
{
    public class Function : AwsFunctionBase
    {
        private string _carrierOptimizationQueueUrl = Environment.GetEnvironmentVariable("CarrierOptimizationQueueURL");
        private string _deviceSyncQueueUrl = Environment.GetEnvironmentVariable("DeviceSyncQueueURL");
        private readonly int DEFAULT_QUEUES_PER_INSTANCE = 5;
        private bool IsUsingRedisCache = false;
        private int QueuesPerInstance = Convert.ToInt32(Environment.GetEnvironmentVariable("QueuesPerInstance"));
        private string ErrorNotificationEmailReceiver = Environment.GetEnvironmentVariable("ErrorNotificationEmailReceiver");
        private SqsService sqsService = new SqsService();
        private int deviceCount = 0;
        private bool isAutoCarrierOptimization = false;
        private string optimizationSessionGuid = null;
        OptimizationAmopApiTrigger optimizationAmopApiTrigger = new OptimizationAmopApiTrigger();
        /// <summary>
        /// Queue Up Carrier Plan Optimization
        /// </summary>
        /// <param name="sqsEvent"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task Handler(SQSEvent sqsEvent, ILambdaContext context)
        {
            KeySysLambdaContext keysysContext = null;
            try
            {
                keysysContext = BaseFunctionHandler(context);

                if (string.IsNullOrEmpty(_carrierOptimizationQueueUrl))
                {
                    _carrierOptimizationQueueUrl = context.ClientContext.Environment["CarrierOptimizationQueueURL"];
                    _deviceSyncQueueUrl = context.ClientContext.Environment["DeviceSyncQueueURL"];
                    QueuesPerInstance = DEFAULT_QUEUES_PER_INSTANCE;
                    ErrorNotificationEmailReceiver = context.ClientContext.Environment["ErrorNotificationEmailReceiver"];
                }

                IsUsingRedisCache = keysysContext.TestRedisConnection();
                InitializeRepositories(context, keysysContext);

                if (sqsEvent?.Records?.Count > 0)
                {
                    var sqsMessage = sqsEvent.Records[0];
                    if (sqsMessage.MessageAttributes.ContainsKey("OptimizationSessionId") && sqsMessage.MessageAttributes.ContainsKey("HasSynced"))
                    {
                        isAutoCarrierOptimization = true;
                    }
                    // manually queued, so let's process the event
                    await ProcessEvent(keysysContext, serviceProviderRepository, sqsEvent);
                }
                else
                {
                    // this is a regularly scheduled execution
                    isAutoCarrierOptimization = true;
                    await QueueJasperServiceProviders(keysysContext);
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"EXCEPTION: {ex.Message} : {ex.StackTrace}");
            }

            CleanUp(keysysContext);
        }

        private async Task QueueJasperServiceProviders(KeySysLambdaContext context)
        {
            LogInfo(context, "SUB", "QueueJasperServiceProviders");
            var jasperServiceProviders = GetJasperServiceProviders(context);
            if (jasperServiceProviders != null)
            {
                foreach (var serviceProvider in jasperServiceProviders)
                {
                    var billingPeriods = GetBillingPeriodsForServiceProviders(context, new List<int> { serviceProvider.ServiceProviderId }, DateTime.UtcNow.Year, DateTime.UtcNow.Month);

                    foreach (var billingPeriod in billingPeriods)
                    {
                        // is it one of the last 7 days of the billing period?
                        if (IsTimeToRun(context, billingPeriod, serviceProvider))
                        {
                            LogInfo(context, "INFO", $"Is time to run; ServiceProviderId={serviceProvider.ServiceProviderId}; Billing Period End: {billingPeriod.BillingPeriodEnd}");
                            await EnqueueCarrierOptimizationSqs(context, serviceProvider.TenantId, billingPeriod);
                        }
                        else
                        {
                            LogInfo(context, "INFO", "Not time to run");
                        }
                    }
                }
            }
            else
            {
                LogInfo(context, "INFO", "No providers to check");
            }
        }

        //Update 
        private bool IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)
        {
            LogInfo(context, "SUB", $"IsTimeToRun({billingPeriod.BillingPeriodEnd},{serviceProvider.ServiceProviderId},{serviceProvider.OptimizationStartHourLocalTime})");

            // is it one of the last 8 days of the billing period?
            var currentTime = DateTime.UtcNow;
            var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentTime, billingPeriod.BillingTimeZone);
            var daysUntilBillingPeriodEnd = billingPeriod.BillingPeriodEnd.Subtract(currentLocalTime).TotalDays;

            LogInfo(context, "INFO", $"Days until billing period end: {daysUntilBillingPeriodEnd}, currentTime: {currentTime}, currentLocalTime: {currentLocalTime}");
            //if (daysUntilBillingPeriodEnd < 8 &&
            //    currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date &&
            //    serviceProvider.OptimizationStartHourLocalTime != null &&
            //    currentLocalTime.Hour == serviceProvider.OptimizationStartHourLocalTime.Value)
            //{
            //    return true;
            //}

            //Update
            if ((daysUntilBillingPeriodEnd < 8 && currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date) ||
            (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && serviceProvider.OptimizationStartHourLocalTime != null))
            {
                if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
                {
                    return true; // Allow continuous runs on the last day from start hour
                }
            }

            // was the override parameter passed (forces execution)
            return context.IsExecutionOverridden;
        }

        private async Task ProcessEvent(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, SQSEvent sqsEvent)
        {
            LogInfo(context, "SUB", "ProcessEvent");
            if (sqsEvent.Records.Count > 0)
            {
                if (sqsEvent.Records.Count == 1)
                {
                    await ProcessEventRecord(context, serviceProviderRepository, sqsEvent.Records[0]);
                }
                else
                {
                    LogInfo(context, "EXCEPTION", $"Expected a single message, received {sqsEvent.Records.Count}");
                }
            }
        }

        private async Task ProcessEventRecord(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, SQSEvent.SQSMessage message)
        {
            var logger = context.logger;
            logger.LogInfo("SUB", "ProcessEventRecord");
            // Separated code flow for recording rate plan sequences and their optimization queues
            if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.RATE_PLAN_SEQUENCES))
            {
                await ProcessRatePlanSequences(context, message);
                return;
            }

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

            int? billingPeriodId = null;
            if (message.MessageAttributes.ContainsKey("BillPeriodId"))
            {
                if (!int.TryParse(message.MessageAttributes["BillPeriodId"].StringValue, out var sqsBillingPeriodId))
                {
                    logger.LogInfo("EXCEPTION", "Invalid Billing Period provided in message");
                    return;
                }

                billingPeriodId = sqsBillingPeriodId;
            }

            int serviceProviderId = int.Parse(message.MessageAttributes["ServiceProviderId"].StringValue);
            int tenantId = int.Parse(message.MessageAttributes["TenantId"].StringValue);

            long optimizationSessionId;
            string additionalData = null;
            if (!message.MessageAttributes.ContainsKey("OptimizationSessionId"))
            {
                var isOptRunning = IsOptimizationRunning(context, tenantId);
                if (!isOptRunning)
                {
                    var billingPeriod = GetBillingPeriod(context, billingPeriodId.Value);
                    optimizationSessionId = await StartOptimizationSession(context, tenantId, billingPeriod);
                    var billPeriodDetails = optimizationAmopApiTrigger.GetBillingPeriodById(context, billingPeriodId.Value);
                    var additionalDataObject = new
                    {
                        data = new
                        {
                            BillPeriodId = billingPeriodId.Value,
                            SiteId = 0,
                            ServiceProviderId = serviceProviderId,
                            OptimizationType = 0,
                            OptimizationFrom = "group",
                            BillingPeriodStartDate = billPeriodDetails.BillingCycleStartDate,
                            BillingPeriodEndDate = billPeriodDetails.BillingCycleEndDate,
                            DeviceCount = 0,
                            TenantId = tenantId,
                        }
                    };
                    additionalData = Newtonsoft.Json.JsonConvert.SerializeObject(additionalDataObject);
                    optimizationSessionGuid = optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId(context, optimizationSessionId);
                    if (isAutoCarrierOptimization)
                    {
                        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 0, "", additionalData);
                    }
                }
                else
                {
                    logger.LogInfo("WARN", "A Carrier Optimization has not been completed so another optimization will not be triggered.");

                    var subject = "[Warning] Carrier Plan Optimization: Optimization Is Already Running";
                    var body = BuildOptRunningAlertEmailBody(context);
                    SendAlertEmail(context, subject, body);
                    return;
                }
            }
            else
            {
                optimizationSessionId = long.Parse(message.MessageAttributes["OptimizationSessionId"].StringValue);
                optimizationSessionGuid = optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId(context, optimizationSessionId);
            }

            if (!message.MessageAttributes.ContainsKey("HasSynced") || !bool.TryParse(message.MessageAttributes["HasSynced"].StringValue, out var hasSynced))
            {
                hasSynced = false;
            }
            if (!hasSynced)
            {
                if (isAutoCarrierOptimization)
                {
                    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 20, "", additionalData);
                }
                logger.LogInfo("INFO", "Have not synced devices and usage already for this optimization run...enqueuing");
                TruncateStagingTables(logger, context.GeneralProviderSettings.JasperDbConnectionString, serviceProviderId);
                // sync anything that has changed in the last month
                DateTime lastSyncDate = DateTime.UtcNow.AddMonths(-1).AddDays(-1);
                var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
                await EnqueueGetDeviceListAsync(_deviceSyncQueueUrl, serviceProviderId, 1, lastSyncDate, awsCredentials, logger, optimizationSessionId);
                return;
            }
            if (isAutoCarrierOptimization)
            {
                int instanceId = 0;
                instanceId = optimizationAmopApiTrigger.GetInstancebySessionId(context, optimizationSessionId.ToString());
                LogInfo(context, "InstanceId", instanceId);
                if (instanceId != 0)
                {
                    deviceCount = optimizationAmopApiTrigger.GetOptimizationDeviceCount(context, instanceId, "M2M");
                    LogInfo(context, "Device Count", deviceCount);
                }
                OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, deviceCount, null, 30, "", additionalData);
            }
            PortalTypes portalType = PortalTypes.M2M;
            if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.PORTAL_TYPE_ID))
            {
                portalType = (PortalTypes)Convert.ToInt32(message.MessageAttributes[SQSMessageKeyConstant.PORTAL_TYPE_ID].StringValue);
            }
            else
            {
                LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.SQS_MESSAGE_ATTRIBUTE_NOT_FOUND, SQSMessageKeyConstant.PORTAL_TYPE_ID) + string.Format(LogCommonStrings.DEFAULTING_SQS_MESSAGE_VALUE_MESSAGE, PortalTypes.M2M.ToString()));
            }

            context.LogInfo(SQSMessageKeyConstant.PORTAL_TYPE_ID, portalType.ToString());
            SetPortalType(portalType);
            ArgumentNullException.ThrowIfNull(billingPeriodId);
            await RunOptimizationByPortalType(context, serviceProviderRepository, billingPeriodId.Value, serviceProviderId, tenantId, optimizationSessionId, portalType, additionalData);
            if (isAutoCarrierOptimization)
            {
                OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 50, "", additionalData);
            }
        }

        private async Task RunOptimizationByPortalType(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, int billingPeriodId, int serviceProviderId, int tenantId, long optimizationSessionId, PortalTypes portalType, string additionalData)
        {
            var logger = context.logger;
            // Start instance
            var billingPeriod = GetBillingPeriod(context, billingPeriodId);
            var integrationAuthenticationId = serviceProviderRepository.GetIntegrationAuthenticationId(serviceProviderId);

            long instanceId;
            instanceId = StartOptimizationInstance(context, tenantId, serviceProviderId, null, null,
                            integrationAuthenticationId, billingPeriod.BillingPeriodStart, billingPeriod.BillingPeriodEnd,
                                    portalType, optimizationSessionId, billingPeriodId, false, null);
            var instance = GetInstance(context, instanceId);

            // Check cache and send email if it is unreachable but configured with a valid connection string 
            if (context.IsRedisConnectionStringValid && !IsUsingRedisCache)
            {
                await LogAndSendConfigurationIssueEmailAsync(context, ErrorNotificationEmailReceiver, optimizationSessionId, instance.Id);
            }
            if (portalType == PortalTypes.M2M)
            {
                await RunOptimization(context, tenantId, serviceProviderId, billingPeriodId, optimizationSessionId, billingPeriod, instance, additionalData, integrationAuthenticationId);
                deviceCount = optimizationAmopApiTrigger.GetOptimizationDeviceCount(context, instance.Id, "M2M");
            }
            else if (portalType == PortalTypes.Mobility)
            {
                await RunMobilityOptimization(context, optimizationMobilityDeviceRepository, tenantId, serviceProviderId, billingPeriodId, optimizationSessionId, billingPeriod, instance, additionalData, integrationAuthenticationId);
                deviceCount = optimizationAmopApiTrigger.GetOptimizationDeviceCount(context, instance.Id, "Mobility");
            }
            else
            {
                OptimizationErrorHandler.OnPortalTypeError(context, PortalType, true);
            }
            if (isAutoCarrierOptimization)
            {
                OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, deviceCount, null, 40, "", additionalData);
            }

        }

        //Update
        private bool IsOptimizationRunning(KeySysLambdaContext context, int tenantId)
        {
            LogInfo(context, "SUB", $"({tenantId})");
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
                    cmd.CommandTimeout = SQLConstant.TimeoutSeconds;
                    conn.Open();

                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        int.TryParse(result.ToString(), out optimizationIdRunning);
                    }
                }
            }

            //Update
            // New logic: allow re-run if today is the last day and last optimization is completed
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
                            cmd.CommandType = CommandType.Text;
                            cmd.CommandText = statusQuery;
                            cmd.Parameters.AddWithValue("@optimizationIdRunning", optimizationIdRunning);
                            conn.Open();

                            var status = cmd.ExecuteScalar();
                            if (status != null && (int)status == (int)OptimizationStatus.Completed)
                            {
                                LogInfo(context, "INFO", $"Last optimization completed. Allowing re-run for session: {optimizationIdRunning}");
                                return false; //Allow a new run
                            }
                        }
                    }
                }
            }

            return optimizationIdRunning >= 0;
        }

        private void TruncateStagingTables(IKeysysLogger logger, string connectionString, int serviceProviderId)
        {
            var errorMessages = new List<string>();
            var deviceStagingRepo = new JasperDeviceStagingRepository();
            deviceStagingRepo.DeleteStagingWithPolicy(logger, connectionString, serviceProviderId, errorMessages);

            var usageStagingRepo = new JasperUsageStagingRepository();
            usageStagingRepo.DeleteStagingWithPolicy(logger, connectionString, serviceProviderId, errorMessages);
        }

        private async Task RunOptimization(KeySysLambdaContext context, int tenantId, int serviceProviderId,
            int billingPeriodId, long optimizationSessionId,
            BillingPeriod billingPeriod, OptimizationInstance instance, string additionalData, int? integrationAuthenticationId)
        {
            LogInfo(context, CommonConstants.SUB, $"({tenantId},{serviceProviderId},{billingPeriodId},{integrationAuthenticationId})");
            var instanceType = (IntegrationType)instance.IntegrationId;

            var usesProration = false;
            // AWXPORT-1212 - Assumes that only Jasper has proration as an option
            if (instanceType == IntegrationType.Jasper
                || instanceType == IntegrationType.POD19
                || instanceType == IntegrationType.TMobileJasper
                || instanceType == IntegrationType.Rogers)
            {
                var jasperProviderSettings = context.SettingsRepo.GetJasperDeviceSettings(serviceProviderId);
                usesProration = jasperProviderSettings.UsesProration;
            }

            LogInfo(context, "INFO", $"Uses Proration: {usesProration}");

            // get carrier rate plans and comm plans
            var commPlans = GetCommPlans(context, serviceProviderId);
            var ratePlans = GetRatePlans(context, serviceProviderId);
            if (commPlans != null && commPlans.Count > 0 && ratePlans != null && ratePlans.Count > 0)
            {
                // get expected SIM Card Count
                int expectedSimCount = GetExpectedOptimizationSimCardCount(context, serviceProviderId, null, billingPeriodId, integrationAuthenticationId, tenantId);

                List<long> commPlanGroupIds = new List<long>();
                int actualSimCount = 0;

                // create comm plan groups
                // each comm plan groups insert optimization sim cards
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
                    else if (groupRatePlans.Count == 0)
                    {
                        LogInfo(context, "WARNING", $"The rate plan count is zero for this comm plan group");
                    }
                    else
                    {
                        //check rate plans 
                        if (groupRatePlans.Any(groupRatePlan => groupRatePlan.DataPerOverageCharge <= 0 || groupRatePlan.OverageRate <= 0))
                        {
                            LogInfo(context, "ERROR", "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate");
                            StopOptimizationInstance(context, instance.Id, OptimizationStatus.CompleteWithErrors);
                            //triggger AMOP2.0 to send error message
                            OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 0, "", additionalData);
                            return;
                        }

                        var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(groupRatePlans, null);
                        var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
                        var ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools);

                        // base device assignment
                        List<string> commPlanNames = commPlanGroup.Select(x => x.CommunicationPlanName).ToList();
                        List<vwOptimizationSimCard> optimizationSimCards = GetOptimizationSimCards(context, commPlanNames,
                            serviceProviderId, null, null, billingPeriod.Id, tenantId);
                        var commGroupSimCardCount = BaseDeviceAssignment(context, instance.Id, commPlanGroupId, serviceProviderId, null, null,
                            commPlanNames, ratePoolCollection, ratePools, optimizationSimCards, billingPeriod, usesProration);

                        actualSimCount += commGroupSimCardCount;

                        // zero sim card => no need to run optimizer
                        // one sim card => swapping between rate plans would be the same as base device assignment
                        //              => already calculate that => no need to run optimizer
                        if (commGroupSimCardCount > 1)
                        {
                            // add rate plans to comm plan group
                            DataTable commGroupRatePlanTable = AddCarrierRatePlansToCommPlanGroup(context, instance.Id, commPlanGroupId, calculatedPlans);

                            // permute rate plans
                            var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);

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

                            CreateQueueRatePlans(context, dtQueueRatePlan);
                        }
                        else
                        {
                            LogInfo(context, "INFO", $"Comm group for the comm plans {string.Join(',', commPlanGroup.ToList())} only have {commGroupSimCardCount} devices. The optimization by permutation logic will not be triggered.");
                            //remove the comm group id from the list so that we won't queue up message for optimizer
                            commPlanGroupIds.Remove(commPlanGroupId);
                        }
                    }
                }

                // check actual vs expected
                if (actualSimCount < expectedSimCount)
                {
                    SendCarrierSimCardCountAlertEmail(context, instance, expectedSimCount, actualSimCount);
                }

                // queue comm plan groups rate plan permutations
                await EnqueueOptimizationRunsAsync(context, instance.Id, commPlanGroupIds, OptimizationChargeType.RateChargeAndOverage, QueuesPerInstance);

                // enqueue cleanup method
                EnqueueCleanup(context, instance.Id, serviceProviderId: serviceProviderId);
            }
            else
            {
                LogInfo(context, "ERROR", "No Comm Groups and/or Rate Plans for this Instance");
                StopOptimizationInstance(context, instance.Id, OptimizationStatus.CompleteWithErrors);
                //triggger AMOP2.0 to send error message
                OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, "No Comm Groups and/or Rate Plans for this Instance", 0, "", additionalData);
            }
        }

        protected async Task RunMobilityOptimization(KeySysLambdaContext context, IOptimizationMobilityDeviceRepository mobilityOptimizationRepository, int tenantId, int serviceProviderId, int billingPeriodId, long optimizationSessionId, BillingPeriod billingPeriod, OptimizationInstance instance, string additionalData, int? integrationAuthenticationId)
        {
            LogInfo(context, CommonConstants.SUB, $"({tenantId},{serviceProviderId},{billingPeriodId},{integrationAuthenticationId})");

            var usesProration = false;
            // Get all carrier rate plans & optimization groups
            var ratePlans = carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), serviceProviderId);

            var optimizationGroups =
                carrierRatePlanRepository.GetValidOptimizationGroupsWithRatePlanIds(ParameterizedLog(context), serviceProviderId);
            if (optimizationGroups?.Count > 0 && ratePlans?.Count > 0)
            {
                // Get expected SIMs Count
                // Also filtered out SIMs with invalid rate plans or don't have a optimization group
                int expectedSimCount = optimizationMobilityDeviceRepository.GetExpectedOptimizationSimCardCount(context, serviceProviderId, null, billingPeriodId, isCarrierOptimization: true, integrationAuthenticationId, tenantId);

                var sameRatePlansCollectionIds = new List<long>();
                int actualSimCount = 0;
                var simCardsByOptimizationGroupIds = mobilityOptimizationRepository.GetMobilityOptimizationSimCardsWithRetry(context, null, serviceProviderId, null, null, billingPeriod.Id, tenantId,
                    isCarrierOptimization: true).GroupBy(x => x.OptimizationGroupId);
                var validOptimizationGroupIds = simCardsByOptimizationGroupIds.Select(x => x.Key).ToList();
                var optimizationGroupsWithDevices = optimizationGroups.Where(x => validOptimizationGroupIds.Contains(x.Id));

                // We process each optimization group since devices usages are pooled by optimization group 
                foreach (var optimizationGroup in optimizationGroupsWithDevices)
                {
                    // Only create comm group since optimization queue is mapped to a comm group
                    long sameRatePlansCollectionId = CreateCommPlanGroup(context, instance.Id);
                    sameRatePlansCollectionIds.Add(sameRatePlansCollectionId);

                    mobilityOptimizationRepository.AddOptimizationGroupToCollection(context, instance.Id, sameRatePlansCollectionId, optimizationGroup);

                    var groupRatePlans = MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup);

                    // Rate plans of an optimization group are limited since we will generate permutation. If more than that, send an error email
                    if (groupRatePlans.Count > OptimizationConstant.MobilityCarrierRatePlanLimit)
                    {
                        LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.OPTIMIZATION_GROUP_RATE_PLAN_LIMIT_ERROR, OptimizationConstant.MobilityCarrierRatePlanLimit, optimizationGroup.Name, optimizationGroup.Id));
                        SendCarrierPlanLimitAlertEmail(context, instance, OptimizationConstant.MobilityCarrierRatePlanLimit);
                    }
                    else if (groupRatePlans.Count == 0)
                    {
                        LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.NO_RATE_PLAN_FOUND_FOR_OPTIMIZATION_GROUP, optimizationGroup.Name, optimizationGroup.Id));
                    }
                    else
                    {
                        // Check valid rate plans
                        if (CheckZeroValueRatePlans(context, instance.Id, groupRatePlans, shouldStopInstance: true))
                        {
                            break;
                        }

                        var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(groupRatePlans, null);
                        var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, usesProration, OptimizationChargeType.RateChargeAndOverage);
                        var ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools, shouldPoolByOptimizationGroup: true);
                        List<vwOptimizationSimCard> optimizationGroupSimCards = GetSimCardsByGroup(simCardsByOptimizationGroupIds, optimizationGroup, groupRatePlans);
                        if (optimizationGroupSimCards?.Count == 0)
                        {
                            LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.NO_DEVICE_FOUND_FOR_OPTIMIZATION_GROUP,
                                optimizationGroup.Name, optimizationGroup.Id));
                            continue;
                        }
                        // Do baseline calculation: assign each device to rate plans with data limit closest to its usage
                        var groupSimCardCount = BaseDeviceAssignment(context, instance.Id, sameRatePlansCollectionId, serviceProviderId, null, null, new() { optimizationGroup.Name }, ratePoolCollection, ratePools, optimizationGroupSimCards, billingPeriod, usesProration, shouldFilterByRatePlanType: true);

                        actualSimCount += groupSimCardCount;

                        // If no sim card => no need to run optimizer
                        // If only one sim card => swapping between rate plans would be the same as base device assignment
                        //                      => already calculate that => no need to run optimizer
                        if (groupSimCardCount > 1)
                        {
                            LogInfo(context, CommonConstants.INFO, string.Format(LogCommonStrings.GENERATING_RATE_PLAN_SEQUENCES_BY_RATE_PLAN_TYPES,
                                groupSimCardCount));
                            // Add rate plans to comm plan group
                            DataTable commGroupRatePlanTable = AddCarrierRatePlansToCommPlanGroup(context, instance.Id, sameRatePlansCollectionId, calculatedPlans);

                            // Permute rate plans
                            var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequencesByRatePlanTypes(ratePoolCollection.RatePools);

                            if (ratePoolSequences.Count > OptimizationConstant.RATE_PLAN_SEQUENCES_FIRST_INSTANCE_LIMIT)
                            {
                                // Remove the comm group id from the list so that we won't queue up message for optimizer
                                sameRatePlansCollectionIds.Remove(sameRatePlansCollectionId);
                                // Save the optimization queues and map them to sequences
                                BulkSaveRatePlanAndSequences(context, serviceProviderId, instance, usesProration, sameRatePlansCollectionId, ratePoolSequences);
                                // Queue up new instance for creating rate plan sequences
                                await SendMessageToCreateQueueRatePlans(context, ratePoolSequences, sameRatePlansCollectionId);
                            }
                            else
                            {
                                SaveRatePlanAndSequences(context, serviceProviderId, instance, usesProration, sameRatePlansCollectionId, commGroupRatePlanTable, ratePoolSequences);
                            }
                        }
                        else
                        {
                            LogInfo(context, CommonConstants.INFO, string.Format(LogCommonStrings.OPTIMIZATION_GROUP_NOT_HAVING_ENOUGH_DEVICE, optimizationGroup.Name, groupSimCardCount));
                            // Remove the comm group id from the list so that we won't queue up message for optimizer
                            sameRatePlansCollectionIds.Remove(sameRatePlansCollectionId);
                        }
                    }
                }

                // Check actual vs expected
                if (actualSimCount < expectedSimCount)
                {
                    SendCarrierSimCardCountAlertEmail(context, instance, expectedSimCount, actualSimCount);
                }

                // Queue comm plan groups rate plan permutations
                await EnqueueOptimizationRunsAsync(context, instance.Id, sameRatePlansCollectionIds, OptimizationChargeType.RateChargeAndOverage, QueuesPerInstance);

                // Enqueue cleanup lambda
                EnqueueCleanup(context, instance.Id);
            }
            else
            {
                LogInfo(context, CommonConstants.ERROR, string.Format(LogCommonStrings.NOT_ENOUGH_OPTIMIZATION_GROUP_OR_RATE_PLAN_FOR_OPTIMIZATION, optimizationGroups?.Count, ratePlans?.Count, instance.Id));
                StopOptimizationInstance(context, instance.Id, OptimizationStatus.CompleteWithErrors);
                //triggger AMOP2.0 to send error message
                OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, string.Format(LogCommonStrings.NOT_ENOUGH_OPTIMIZATION_GROUP_OR_RATE_PLAN_FOR_OPTIMIZATION), 0, "", additionalData);
            }
        }

        private static List<vwOptimizationSimCard> GetSimCardsByGroup(IEnumerable<IGrouping<int?, vwOptimizationSimCard>> allOptimizationSimCards, OptimizationGroup optimizationGroup, List<RatePlan> groupRatePlans)
        {
            var ratePlanTypes = groupRatePlans.Select(x => x.RatePlanTypeId);
            List<vwOptimizationSimCard> optimizationGroupSimCards = new List<vwOptimizationSimCard>();
            foreach (var group in allOptimizationSimCards)
            {
                if (group.Key == optimizationGroup.Id)
                {
                    optimizationGroupSimCards = group.Where(x => ratePlanTypes.Contains(x.RatePlanTypeId)).ToList();
                }
            }

            return optimizationGroupSimCards;
        }

        private void SaveRatePlanAndSequences(KeySysLambdaContext context, int serviceProviderId, OptimizationInstance instance, bool usesProration, long sameRatePlansCollectionId, DataTable commGroupRatePlanTable, List<RatePlanSequence> ratePoolSequences)
        {
            DataTable dtQueueRatePlan = new DataTable();
            dtQueueRatePlan.Columns.Add(CommonColumnNames.QueueId, typeof(long));
            dtQueueRatePlan.Columns.Add(CommonColumnNames.CommGroupRatePlanId, typeof(long));
            dtQueueRatePlan.Columns.Add(CommonColumnNames.SequenceOrder, typeof(int));
            dtQueueRatePlan.Columns.Add(CommonColumnNames.CreatedBy);
            dtQueueRatePlan.Columns.Add(CommonColumnNames.CreatedDate, typeof(DateTime));

            foreach (var ratePoolSequence in ratePoolSequences)
            {
                // add queue for rate plan permutation
                var queueId = CreateQueue(context, instance.Id, sameRatePlansCollectionId, serviceProviderId, usesProration);

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

            CreateQueueRatePlans(context, dtQueueRatePlan);
        }

        private List<RatePlan> RatePlansForGroup(List<RatePlan> ratePlans, IGrouping<string, CommPlan> commPlanGroup)
        {
            var ratePlanIds = commPlanGroup.Key;
            var ratePlanIdList = ratePlanIds.Split(',').Distinct().ToList();
            List<RatePlan> groupRatePlans = new List<RatePlan>();
            foreach (var planId in ratePlanIdList)
            {
                var ratePlan = ratePlans.FirstOrDefault(x => x.Id.ToString() == planId);
                if (ratePlan.Id.ToString() == planId)
                {
                    groupRatePlans.Add(ratePlan);
                }
            }

            return groupRatePlans;
        }

        private void AddCommPlansToCommPlanGroup(KeySysLambdaContext context, long instanceId, long commPlanGroupId, IGrouping<string, CommPlan> commPlanGroup)
        {
            LogInfo(context, "SUB", "AddCommPlansToCommPlanGroup");

            DataTable table = new DataTable();
            table.Columns.Add("InstanceId", typeof(long));
            table.Columns.Add("CommGroupId", typeof(long));
            table.Columns.Add("CommPlanId", typeof(int));
            table.Columns.Add("CreatedBy");
            table.Columns.Add("CreatedDate", typeof(DateTime));

            foreach (CommPlan plan in commPlanGroup)
            {
                var dr = table.NewRow();

                dr[0] = instanceId;
                dr[1] = commPlanGroupId;
                dr[2] = plan.Id;
                dr[3] = "System";
                dr[4] = DateTime.UtcNow;

                table.Rows.Add(dr);
            }

            List<SqlBulkCopyColumnMapping> columnMappings = new List<SqlBulkCopyColumnMapping>()
            {
                new SqlBulkCopyColumnMapping("InstanceId", "InstanceId"),
                new SqlBulkCopyColumnMapping("CommGroupId", "CommGroupId"),
                new SqlBulkCopyColumnMapping("CommPlanId", "CommPlanId"),
                new SqlBulkCopyColumnMapping("CreatedBy", "CreatedBy"),
                new SqlBulkCopyColumnMapping("CreatedDate", "CreatedDate")
            };

            var logMessage = SqlHelper.SqlBulkCopy(context.ConnectionString, table, "OptimizationCommGroup_CommPlan", columnMappings);
            LogInfo(context, logMessage);
        }

        private RevCustomer GetRevCustomerById(KeySysLambdaContext context, Guid customerGuidId)
        {
            RevCustomer cust = new RevCustomer();
            cust.id = customerGuidId;

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
                        cust.RevCustomerId = rdr["RevCustomerId"].ToString();
                        cust.CustomerName = rdr["CustomerName"].ToString();
                    }

                    conn.Close();
                }
            }

            return cust;
        }

        private void SendCarrierSimCardCountAlertEmail(KeySysLambdaContext context, OptimizationInstance instance, int expectedSimCardCount, int actualSimCardCount)
        {
            var credentials = context.GeneralProviderSettings.AwsSesCredentials;
            using (var client = new AmazonSimpleEmailServiceClient(credentials, RegionEndpoint.USEast1))
            {
                var message = new MimeMessage();

                message.From.Add(MailboxAddress.Parse(context.OptimizationSettings.FromEmailAddress));
                var recipientAddressList = context.OptimizationSettings.ToEmailAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                message.Subject = "Carrier Plan Optimization Warning: SIM Counts not Expected";

                foreach (var recipientAddress in recipientAddressList)
                {
                    message.To.Add(MailboxAddress.Parse(recipientAddress));
                }

                message.Body = BuildSimCardCountAlertEmailBody(context, instance, expectedSimCardCount, actualSimCardCount).ToMessageBody();
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
                    LogInfo(context, "Error Sending SIM Card Alert Email", ex.Message);
                }
            }
        }

        private BodyBuilder BuildSimCardCountAlertEmailBody(KeySysLambdaContext context, OptimizationInstance instance, int expectedSimCardCount, int actualSimCardCount)
        {
            LogInfo(context, "SUB", $"BuildSimCardCountAlertEmailBody({instance.Id},{expectedSimCardCount},{actualSimCardCount})");

            var body = new BodyBuilder()
            {
                HtmlBody = $"<div>The SIM Card count for Instance {instance.Id} is {actualSimCardCount}. This is less than the expected count of {expectedSimCardCount}. Please review the results of this Optimization to ensure that all eligible SIM Cards were included.</div>",
                TextBody = $"The SIM Card count for Instance {instance.Id} is {actualSimCardCount}. This is less than the expected count of {expectedSimCardCount}. Please review the results of this Optimization to ensure that all eligible SIM Cards were included."
            };

            return body;
        }

        private void SendCarrierPlanLimitAlertEmail(KeySysLambdaContext context, OptimizationInstance instance, int ratePlanLimit = OptimizationConstant.RatePlanLimit)
        {
            var credentials = context.GeneralProviderSettings.AwsSesCredentials;
            using (var client = new AmazonSimpleEmailServiceClient(credentials, RegionEndpoint.USEast1))
            {
                var message = new MimeMessage();

                message.From.Add(MailboxAddress.Parse(context.OptimizationSettings.FromEmailAddress));
                var recipientAddressList = context.OptimizationSettings.ToEmailAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                message.Subject = "Carrier Plan optimization Error: Rate Plans limit exceeded";

                foreach (var recipientAddress in recipientAddressList)
                {
                    message.To.Add(MailboxAddress.Parse(recipientAddress));
                }

                message.Body = BuildAlertEmailBody(context, instance, ratePlanLimit).ToMessageBody();
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
                    LogInfo(context, "Error Sending Rate Plan Limit Alert Email", ex.Message);
                }
            }
        }

        private BodyBuilder BuildAlertEmailBody(KeySysLambdaContext context, OptimizationInstance instance, int ratePlanLimit = OptimizationConstant.RatePlanLimit)
        {
            LogInfo(context, "SUB", $"BuildCarrierRatePlanOptimizationLimitAlertBody({instance.Id})");

            var body = new BodyBuilder()
            {
                HtmlBody = $"<div>The rate plan count for Instance {instance.Id} has exceeded the limit of {ratePlanLimit}. Please log in to the Portal to limit the plans selected to {ratePlanLimit} or lower.</div>",
                TextBody = $"The rate plan count for Instance {instance.Id} has exceeded the limit of {ratePlanLimit}. Please log in to the Portal to limit the plans selected to {ratePlanLimit} or lower"
            };

            return body;
        }

        internal struct JasperProviderLite
        {
            internal int ServiceProviderId { get; set; }
            internal int TenantId { get; set; }
            internal int? OptimizationStartHourLocalTime { get; set; }
        }

        private IList<JasperProviderLite> GetJasperServiceProviders(KeySysLambdaContext context)
        {
            List<JasperProviderLite> jasperServiceProviders = new List<JasperProviderLite>();
            using (var conn = new SqlConnection(context.ConnectionString))
            {
                using (var cmd = new SqlCommand("usp_Jasper_Get_Active_ServiceProviders", conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    conn.Open();

                    int serviceProviderId;
                    int tenantId;
                    int? optimizationStartHourLocalTime;

                    SqlDataReader rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        serviceProviderId = (int)rdr["ServiceProviderId"];
                        tenantId = (int)rdr["TenantId"];
                        optimizationStartHourLocalTime = rdr.IsDBNull(rdr.GetOrdinal("OptimizationStartHourLocalTime")) ? null : new int?((int)rdr["OptimizationStartHourLocalTime"]);
                        jasperServiceProviders.Add(new JasperProviderLite() { ServiceProviderId = serviceProviderId, TenantId = tenantId, OptimizationStartHourLocalTime = optimizationStartHourLocalTime });
                    }

                    conn.Close();
                }
            }

            return jasperServiceProviders;
        }

        private async Task EnqueueCarrierOptimizationSqs(KeySysLambdaContext context, int tenantId, BillingPeriod billingPeriod)
        {
            LogInfo(context, "SUB", "EnqueueCarrierOptimizationSqs(...)");
            LogInfo(context, "DEBUG", $"BillPeriodId: {billingPeriod.Id})");
            LogInfo(context, "DEBUG", $"BillYear: {billingPeriod.BillingPeriodYear})");
            LogInfo(context, "DEBUG", $"BillMonth: {billingPeriod.BillingPeriodMonth})");
            LogInfo(context, "DEBUG", $"ServiceProviderId: {billingPeriod.ServiceProviderId})");
            LogInfo(context, "DEBUG", $"TenantId: {tenantId})");
            LogInfo(context, "DEBUG", $"HasSynced: {false})");
            try
            {
                var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
                using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
                {

                    var requestMsgBody = $"Carrier to optimize is for Billing Period {billingPeriod.BillingPeriodYear}/{billingPeriod.BillingPeriodMonth}";
                    var request = new SendMessageRequest
                    {
                        MessageAttributes = new Dictionary<string, MessageAttributeValue>
                        {
                            {
                                "BillYear", new MessageAttributeValue
                                    { DataType = "String", StringValue = billingPeriod.BillingPeriodYear.ToString()}
                            },
                            {
                                "BillMonth", new MessageAttributeValue
                                    { DataType = "String", StringValue = billingPeriod.BillingPeriodMonth.ToString()}
                            },
                            {
                                "ServiceProviderId", new MessageAttributeValue
                                    { DataType = "String", StringValue = billingPeriod.ServiceProviderId.ToString()}
                            },
                            {
                                "TenantId", new MessageAttributeValue
                                    { DataType = "String", StringValue = tenantId.ToString()}
                            },
                            {
                                "BillPeriodId", new MessageAttributeValue
                                    { DataType = "String", StringValue = billingPeriod.Id.ToString()}
                            },
                            {
                                "HasSynced", new MessageAttributeValue
                                    { DataType = "String", StringValue = false.ToString()}
                            }
                        },
                        MessageBody = requestMsgBody,
                        QueueUrl = _carrierOptimizationQueueUrl
                    };

                    var response = await client.SendMessageAsync(request);
                    if ((int)response.HttpStatusCode < 200 || (int)response.HttpStatusCode > 299)
                    {
                        LogInfo(context, "EXCEPTION", $"Error Queuing Optimization: {response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogInfo(context, "EXCEPTION", $"Error Queuing Optimization: {ex.Message} {ex.StackTrace}");
            }
        }

        private static async Task EnqueueGetDeviceListAsync(string destinationQueueUrl, int serviceProviderId, int pageNumber, DateTime lastSyncDate, AWSCredentials awsCredentials, IKeysysLogger logger, long optSessionId)
        {
            var delaySeconds = 5;
            var nextStep = JasperDeviceSyncNextStep.DEVICE_USAGE_BY_RATE_PLAN;
            logger.LogInfo("SUB", "EnqueueGetDeviceListAsync(");
            logger.LogInfo("DEBUG", $"QueueUrl: {destinationQueueUrl}");
            logger.LogInfo("DEBUG", $"PageNumber: {pageNumber}");
            logger.LogInfo("DEBUG", $"LastSyncDate: {lastSyncDate}");
            logger.LogInfo("DEBUG", $"ServiceProviderId: {serviceProviderId}");
            logger.LogInfo("DEBUG", $"NextStep: {nextStep}");
            logger.LogInfo("DEBUG", $"OptimizationSessionId: {optSessionId}");

            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var request = new SendMessageRequest
                {
                    DelaySeconds = delaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "PageNumber", new MessageAttributeValue {DataType = "String", StringValue = pageNumber.ToString()}
                        },
                        {
                            "LastSyncDate", new MessageAttributeValue {DataType = "String", StringValue = lastSyncDate.ToString()}
                        },
                        {
                            "ServiceProviderId", new MessageAttributeValue {DataType = "String", StringValue = serviceProviderId.ToString()}
                        },
                        {
                            "NextStep", new MessageAttributeValue {DataType = "String", StringValue = nextStep.ToString()}
                        },
                        {
                            "OptimizationSessionId", new MessageAttributeValue {DataType = "String", StringValue = optSessionId.ToString()}
                        }
                    },
                    MessageBody = $"Next page number is {pageNumber} for the last sync date {lastSyncDate}",
                    QueueUrl = destinationQueueUrl
                };
                var response = await client.SendMessageAsync(request);
                if ((int)response.HttpStatusCode < 200 || (int)response.HttpStatusCode > 299)
                {
                    logger.LogInfo("EXCEPTION", $"Error enqueuing device sync: {response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
            }
        }

        private void SendAlertEmail(KeySysLambdaContext context, string subject, BodyBuilder body)
        {
            LogInfo(context, "SUB", string.Empty);

            var credentials = context.GeneralProviderSettings.AwsSesCredentials;
            using (var client = new AmazonSimpleEmailServiceClient(credentials, RegionEndpoint.USEast1))
            {
                var message = new MimeMessage();

                message.From.Add(MailboxAddress.Parse(context.OptimizationSettings.FromEmailAddress));
                var recipientAddressList = context.OptimizationSettings.ToEmailAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                message.Subject = subject;

                foreach (var recipientAddress in recipientAddressList)
                {
                    message.To.Add(MailboxAddress.Parse(recipientAddress));
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
                    LogInfo(context, "Error Sending Alert Email", ex.Message);
                    LogInfo(context, "The content email", body.TextBody);
                }
            }
        }

        private BodyBuilder BuildOptRunningAlertEmailBody(KeySysLambdaContext context)
        {
            LogInfo(context, "SUB", string.Empty);

            var body = new BodyBuilder()
            {
                HtmlBody = $"<div>[Warning] Carrier Plan Optimization: A Carrier optimization job is already running so another Carrier optimization job will not be started.</div>",
                TextBody = $"[Warning] Carrier Plan Optimization: A Carrier optimization job is already running so another Carrier optimization job will not be started."
            };

            return body;
        }

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
            else
            {
                LogInfo(context, CommonConstants.ERROR, $"Invalid SQS Message for processing rate plan sequences: {JsonSerializer.Serialize(message.MessageAttributes)}");
            }
        }

        private static DataTable BuildOptimizationQueueTable()
        {
            DataTable table = new DataTable();
            //InstanceId, CommPlanGroupId, RunStatusId, ServiceProviderId, UsesProration, CreatedBy, CreatedDate, IsBillInAdvance
            table.Columns.Add(CommonColumnNames.InstanceId, typeof(long));
            table.Columns.Add(CommonColumnNames.CommPlanGroupId, typeof(int));
            table.Columns.Add(CommonColumnNames.RunStatusId, typeof(decimal));
            table.Columns.Add(CommonColumnNames.ServiceProviderId, typeof(int));
            table.Columns.Add(CommonColumnNames.UsesProration, typeof(int));
            table.Columns.Add(CommonColumnNames.IsBillInAdvance, typeof(int));
            return table;
        }

        private void BulkSaveRatePlanAndSequences(KeySysLambdaContext context, int serviceProviderId, OptimizationInstance instance, bool usesProration, long sameRatePlansCollectionId, List<RatePlanSequence> ratePoolSequences)
        {
            var queueIds = BulkCreateQueue(context, instance.Id, sameRatePlansCollectionId, serviceProviderId, usesProration, ratePoolSequences.Count, instance.UseBillInAdvance);
            if (queueIds == null || queueIds.Count < ratePoolSequences.Count)
            {
                throw new InvalidOperationException($"Only {queueIds?.Count} {nameof(queueIds)} created. The number of queue Ids should match the number of sequences {ratePoolSequences.Count}");
            }
            for (int i = 0; i < ratePoolSequences.Count; i++)
            {
                var ratePoolSequence = ratePoolSequences[i];
                // add queue for rate plan permutation
                var queueId = queueIds[i];
                ratePoolSequence.QueueId = queueId;
                ratePoolSequences[i] = ratePoolSequence;
            }
            ;
        }

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
            //get queueIds here
            var parameters = new List<SqlParameter>()
            {
                new SqlParameter(CommonSQLParameterNames.COMM_GROUP_ID, commPlanGroupId),
                new SqlParameter(CommonSQLParameterNames.RUN_STATUS_ID, OptimizationStatus.NotStarted),
            };
            var queueIds = SqlQueryHelper.ExecuteStoredProcedureWithListResult(ParameterizedLog(context), context.ConnectionString, SQLConstant.StoredProcedureName.GET_OPTIMIZATION_QUEUE_IDS_BY_COMM_GROUP_ID, ReadQueueId, parameters, SQLConstant.ShortTimeoutSeconds);
            return queueIds;
        }

        public DataRow AddOptimizationQueueRow(DataTable dataTable, long instanceId, long commPlanGroupId, int? serviceProviderId, bool usesProration, bool isBillInAdvance = false)
        {
            var dataRow = dataTable.NewRow();
            //InstanceId, CommPlanGroupId, RunStatusId, ServiceProviderId, UsesProration, CreatedBy, CreatedDate, 
            dataRow[CommonColumnNames.InstanceId] = instanceId;
            dataRow[CommonColumnNames.CommPlanGroupId] = commPlanGroupId;
            dataRow[CommonColumnNames.RunStatusId] = OptimizationStatus.NotStarted;
            dataRow[CommonColumnNames.ServiceProviderId] = serviceProviderId;
            dataRow[CommonColumnNames.UsesProration] = usesProration;
            dataRow[CommonColumnNames.IsBillInAdvance] = isBillInAdvance;
            return dataRow;
        }

        public long ReadQueueId(SqlDataReader dataReader)
        {
            var columns = dataReader.GetColumnsFromReader();
            return dataReader.LongFromReader(columns, CommonColumnNames.Id);
        }

        private async Task SendMessageToCreateQueueRatePlans(KeySysLambdaContext context, List<RatePlanSequence> ratePoolSequences, long commGroupId)
        {
            LogInfo(context, CommonConstants.SUB, $"{nameof(ratePoolSequences.Count)}: {ratePoolSequences.Count}");
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
    }
}
