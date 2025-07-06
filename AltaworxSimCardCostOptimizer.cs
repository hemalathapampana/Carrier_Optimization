using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Altaworx.SimCard.Cost.Optimizer.Core;
using Altaworx.SimCard.Cost.Optimizer.Core.Enumerations;
using Altaworx.SimCard.Cost.Optimizer.Core.Factories;
using Altaworx.SimCard.Cost.Optimizer.Core.Helpers;
using Altaworx.SimCard.Cost.Optimizer.Core.Models;
using Altaworx.SimCard.Cost.Optimizer.Core.Repositories.CarrierRatePlan;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amop.Core.Constants;
using Amop.Core.Helpers;
using Amop.Core.Models;
using Amop.Core.Resilience;
using Microsoft.Data.SqlClient;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace Altaworx.SimCard.Cost.Optimizer
{
    public class Function : AwsFunctionBase
    {
        private const int DEFAULT_SANITY_CHECK_TIME_LIMIT = 180;
        private int SanityCheckTimeLimit = Convert.ToInt32(Environment.GetEnvironmentVariable("SanityCheckTimeLimit"));
        private bool IsUsingRedisCache = false;
        /// <summary>
        /// This function processes an optimization queue item
        /// </summary>
        /// <param name="sqsEvent">SQS Message</param>
        /// <param name="context">Lambda Context</param>
        /// <returns></returns>

        //statuses that indicate the queue have been processed by another Optimizer instance (SQS 'at-least-once' delivery)
        private static readonly List<OptimizationStatus> QUEUE_FINISHED_STATUSES = new List<OptimizationStatus>(){
            OptimizationStatus.CleaningUp,
            OptimizationStatus.CompleteWithSuccess,
            OptimizationStatus.CompleteWithErrors
        };

        public async Task Handler(SQSEvent sqsEvent, ILambdaContext context)
        {
            KeySysLambdaContext keysysContext = null;
            try
            {
                keysysContext = BaseFunctionHandler(context);
                if (SanityCheckTimeLimit == 0)
                {
                    SanityCheckTimeLimit = DEFAULT_SANITY_CHECK_TIME_LIMIT;
                }

                //if a redis cache connection string is set but not reachable => considered as an error & continue without cache
                IsUsingRedisCache = keysysContext.TestRedisConnection();
                if (keysysContext.IsRedisConnectionStringValid && !IsUsingRedisCache)
                {
                    //only log and no email since there are many instances of this lambda during the calculation or else the email receiver will be spam with error notices.
                    var errorMessage = "Redis cache is configured but not reachable. Proceeding without cache.";
                    LogInfo(keysysContext, "EXCEPTION", errorMessage);
                }

                InitializeRepositories(context, keysysContext);

                await ProcessEvent(keysysContext, sqsEvent);
            }
            catch (Exception ex)
            {
                LogInfo(keysysContext, "EXCEPTION", ex.Message + " " + ex.StackTrace);
            }

            base.CleanUp(keysysContext);
        }

        private async Task ProcessEvent(KeySysLambdaContext context, SQSEvent sqsEvent)
        {
            LogInfo(context, "SUB", "ProcessEvent");
            if (sqsEvent.Records.Count > 0)
            {
                if (sqsEvent.Records.Count == 1)
                {
                    await ProcessEventRecord(context, sqsEvent.Records[0]);
                }
                else
                {
                    LogInfo(context, "EXCEPTION", $"Expected a single message, received {sqsEvent.Records.Count}");
                }
            }
        }

        private async Task ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
        {
            LogInfo(context, "SUB", "ProcessEventRecord");
            LogInfo(context, "INFO", $"Message attributes: {string.Join(Environment.NewLine, message.MessageAttributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value.StringValue))}");
            if (!message.MessageAttributes.ContainsKey("SkipLowerCostCheck") ||
                !bool.TryParse(message.MessageAttributes["SkipLowerCostCheck"].StringValue, out var skipLowerCostCheck))
            {
                skipLowerCostCheck = false;
            }

            OptimizationChargeType chargeType = OptimizationChargeType.RateChargeAndOverage;
            if (message.MessageAttributes.ContainsKey("ChargeType") && int.TryParse(message.MessageAttributes["ChargeType"].StringValue, out var intChargeType))
            {
                chargeType = (OptimizationChargeType)intChargeType;
            }

            if (message.MessageAttributes.ContainsKey("QueueIds"))
            {
                var messageId = message.MessageId;
                var queueIdStrings = message.MessageAttributes["QueueIds"].StringValue.Split(',').ToList();
                var queueIds = queueIdStrings.Select(long.Parse).ToList();
                if (message.MessageAttributes.ContainsKey("IsChainingProcess")
                    && bool.TryParse(message.MessageAttributes["IsChainingProcess"].StringValue, out var isChainingOptimization)
                    && isChainingOptimization)
                {
                    if (!context.IsRedisConnectionStringValid)
                    {
                        LogInfo(context, "EXCEPTION", $"No cache connection string is setup. Stopping process.");
                        return;
                    }
                    await ProcessQueuesContinue(context, queueIds, messageId, skipLowerCostCheck, chargeType);
                }
                else
                {
                    await ProcessQueues(context, queueIds, messageId, skipLowerCostCheck, chargeType);
                }

            }
            else
            {
                LogInfo(context, "EXCEPTION", $"No Queue Ids provided in message");
            }
        }

        private async Task ProcessQueues(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
        {
            LogInfo(context, "SUB", $"ProcessQueues(,,{messageId},{skipLowerCostCheck},{chargeType})");

            var isFirstId = true;
            List<Core.SimCard> simCards = new List<Core.SimCard>();
            RatePoolCollection ratePoolCollection = null;
            List<RatePlanSequence> ratePoolSequences = new List<RatePlanSequence>();
            long commPlanGroupId = 0;
            string accountNumber = null;
            int? amopCustomerId = null;
            long instanceId = 0;
            OptimizationInstance instance = null;

            foreach (long queueId in queueIds)
            {
                var queue = GetQueue(context, queueId);

                //check if queue found & valid status for process
                if (queue.Id <= 0)
                {
                    LogInfo(context, "EXCEPTION", $"Could not find queue with id {queueId}. Continue to process next queue.");
                    continue;
                }

                if (QUEUE_FINISHED_STATUSES.Contains(queue.RunStatusId))
                {
                    LogInfo(context, "WARNING", $"Duplicated queue processing request for queue with id {queueId}. Continue to process next queue.");
                    continue;
                }

                if (instance == null)
                {
                    instance = GetInstance(context, queue.InstanceId);
                }

                if (instance.Id <= 0)
                {
                    throw new Exception($"Instance with id {instance.Id} not found.");
                }

                SetPortalType(instance.PortalType);

                var billingPeriod = new BillingPeriod(instance.BillingPeriodIdByPortalType.GetValueOrDefault(0), instance.ServiceProviderId.GetValueOrDefault(), instance.BillingPeriodEndDate.Year, instance.BillingPeriodEndDate.Month, instance.BillingPeriodEndDate.Day, instance.BillingPeriodEndDate.Hour, context.OptimizationSettings.BillingTimeZone, instance.BillingPeriodEndDate);

                accountNumber = null;
                if (instance.RevCustomerId != null)
                {
                    accountNumber = GetRevAccountNumber(context, instance.RevCustomerId.Value);
                }

                amopCustomerId = instance.AMOPCustomerId;

                List<RatePlan> queueRatePlans = carrierRatePlanRepository.GetQueueRatePlans(ParameterizedLog(context), new List<long> { queueId });
                List<int> ratePlanIdSequence = queueRatePlans.Select(x => x.Id).ToList();
                ratePoolSequences.Add(new RatePlanSequence() { QueueId = queueId, RatePlanIds = ratePlanIdSequence });

                if (isFirstId)
                {
                    commPlanGroupId = queue.CommPlanGroupId;
                    // If M2M carrier optimization, use comm plans for optimization
                    var commPlans = new List<string>();
                    if (instance.PortalType == PortalTypes.M2M && !instance.IsCustomerOptimization)
                    {
                        commPlans = GetCommPlansForCommGroup(context, queue.CommPlanGroupId);
                    }
                    // If Mobility carrier optimization, use optimization groups
                    var optimizationGroups = new List<OptimizationGroup>();
                    if (instance.PortalType == PortalTypes.Mobility && !instance.IsCustomerOptimization)
                    {
                        optimizationGroups = carrierRatePlanRepository.GetOptimizationGroupsByCommGroupId(ParameterizedLog(context), queue.CommPlanGroupId);
                    }
                    // If Customer optimization, get customer rate pools
                    int? customerRatePoolId = null;
                    if (instance.IsCustomerOptimization)
                    {
                        customerRatePoolId = GetCustomerRatePoolsByCommGroupId(context, queue.CommPlanGroupId);
                    }
                    // If no customer rate pool -> must optimize using existing implementation (not filter by rate plan code)
                    var shouldFilterByRatePlanCode = false;

                    if (IsUsingRedisCache)
                    {
                        simCards = RedisCacheHelper.GetSimCardsFromCache(context, instance.Id, commPlans, commPlanGroupId,
                                                    () => GetSimCardsByPortalType(context, instance, queue.ServiceProviderId, billingPeriod, instance.PortalType, commPlanGroupId, commPlans, optimizationGroups));
                    }
                    else
                    {
                        simCards = GetSimCardsByPortalType(context, instance, queue.ServiceProviderId, billingPeriod, instance.PortalType, commPlanGroupId, commPlans, optimizationGroups);
                    }

                    if (simCards == null || simCards.Count <= 0)
                    {
                        LogInfo(context, "EXCEPTION", $"No devices found. Stopping process");
                        return;
                    }

                    var avgUsage = simCards.Count > 0 ? simCards.Sum(x => x.CycleDataUsageMB) / simCards.Count : 0;
                    var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(queueRatePlans, avgUsage);
                    var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, queue.UsesProration, chargeType);
                    var shouldPoolByOptimizationGroup = (instance.PortalType == PortalTypes.Mobility || instance.IsCustomerOptimization) && ratePools.Any(x => x.RatePlan.AllowsSimPooling); ;
                    ratePoolCollection = RatePoolCollectionFactory.CreateRatePoolCollection(ratePools, shouldPoolByOptimizationGroup, customerRatePoolId);

                    isFirstId = false;
                }

                StartQueue(context, queueId, messageId);
            }

            //indicated at least 1 queue was processed
            if (!isFirstId)
            {
                var remainingSeconds = (int)Math.Floor(context.LambdaContext.RemainingTime.TotalSeconds);
                LogInfo(context, "INFO", $"Remaining run time: {remainingSeconds} seconds.");

                // each run will have 4 sequential calculation with strategy based on a pair of attributes SimCardGrouping and RemainingAssignmentOrder
                // No Grouping + Largest To Smallest
                // No Grouping + Smallest To Largest
                // Group By Communication Plan + Largest To Smallest
                // Group By Communication Plan + Smallest To Largest
                // => stop at the first calculation if there is cache => continue with the next calculation on new lambda instance
                var shouldFilterByRatePlanType = instance.PortalType == PortalTypes.Mobility && !instance.IsCustomerOptimization;
                var shouldPoolUsageBetweenRatePlans = (instance.PortalType == PortalTypes.Mobility || instance.IsCustomerOptimization) && ratePoolCollection.IsPooled;
                var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
                    instance.PortalType,
                    shouldFilterByRatePlanType,
                    shouldPoolUsageBetweenRatePlans);
                assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                                            context.OptimizationSettings.BillingTimeZone,
                                            false,
                                            false,
                                            ratePoolSequences);

                await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
            }
        }

        private static List<SimCardGrouping> GetSimCardGroupingByPortalType(PortalTypes portalType, bool isCustomerOptimization)
        {
            if (portalType == PortalTypes.Mobility || isCustomerOptimization)
            {
                return new List<SimCardGrouping> { SimCardGrouping.NoGrouping };
            }
            else
            {
                return new List<SimCardGrouping> {
                        SimCardGrouping.NoGrouping,
                        SimCardGrouping.GroupByCommunicationPlan };
            }
        }

        private List<Core.SimCard> GetSimCardsByPortalType(KeySysLambdaContext context, OptimizationInstance instance, int? serviceProviderId, BillingPeriod billingPeriod, PortalTypes portalType, long commPlanGroupId, List<string> commPlans = null, List<OptimizationGroup> optimizationGroups = null)
        {
            if (portalType == PortalTypes.M2M)
            {
                return GetSimCards(context, instance.Id, serviceProviderId, commPlans, billingPeriod, commPlanGroupId, instance.IsCustomerOptimization);
            }
            else if (portalType == PortalTypes.Mobility)
            {
                var optimizationGroupIds = optimizationGroups.Select(x => x.Id).ToList();
                return optimizationMobilityDeviceRepository.GetOptimizationMobilityDevices(context, instance.Id, serviceProviderId, optimizationGroupIds, billingPeriod, commPlanGroupId, instance.IsCustomerOptimization);
            }
            else if (portalType == PortalTypes.CrossProvider)
            {
                return crossProviderOptimizationRepository.GetCrossProviderOptimizationDevices(ParameterizedLog(context), instance.Id, billingPeriod, commPlanGroupId, instance.IsCustomerOptimization);
            }
            else
            {
                OptimizationErrorHandler.OnPortalTypeError(context, instance.PortalType, true);
                return new List<Core.SimCard>();
            }
        }

        private async Task ProcessQueuesContinue(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
        {
            LogInfo(context, "SUB", $"(,,{messageId},{skipLowerCostCheck},{chargeType})");

            if (queueIds.Count() <= 0)
            {
                LogInfo(context, "ERROR", $"No Queue Ids included. Stopping process.");
                return;
            }

            //reference a queue to get resources since they will share the same info
            var referenceQueueId = queueIds.First();
            var queue = GetQueue(context, referenceQueueId);

            //check if queue found & valid status for process
            if (queue.Id <= 0)
            {
                LogInfo(context, "EXCEPTION", $"Could not find queue with id {referenceQueueId}. Continue to process next queue.");
                return;
            }

            if (QUEUE_FINISHED_STATUSES.Contains(queue.RunStatusId))
            {
                LogInfo(context, "WARNING", $"Duplicated queue processing request for queue with id {referenceQueueId}. Continue to process next queue.");
                return;
            }

            var instance = GetInstance(context, queue.InstanceId);
            var amopCustomerId = instance.AMOPCustomerId;
            string accountNumber = null;

            if (instance.RevCustomerId != null)
            {
                accountNumber = GetRevAccountNumber(context, instance.RevCustomerId.Value);
            }

            var commPlanGroupId = queue.CommPlanGroupId;

            // read assigner from cache
            var assigner = RedisCacheHelper.GetPartialAssignerFromCache(context, queueIds, context.OptimizationSettings.BillingTimeZone);

            // if cache not found => consider done
            // if cache is found but complete => save the result
            if (assigner == null)
            {
                return;
            }
            else
            {
                assigner.SetLambdaContext(context.LambdaContext);
                assigner.SetLambdaLogger(context.logger);
                // call assignSimCardsContinue to continue the processing
                assigner.AssignSimCardsContinue(context.OptimizationSettings.BillingTimeZone, false);
            }
            await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
        }

        private async Task WrapUpCurrentInstance(KeySysLambdaContext context, List<long> queueIds, bool skipLowerCostCheck, OptimizationChargeType chargeType, int? amopCustomerId, string accountNumber, long commPlanGroupId, RatePoolAssigner assigner)
        {
            LogInfo(context, "SUB", $"(,{string.Join(',', queueIds)},)");
            // if complete => save
            // else record to cache & send sqs message
            if (!assigner.IsCompleted && context.IsRedisConnectionStringValid && IsUsingRedisCache)
            {
                //save to cache the assigner
                var remainingQueueIds = RedisCacheHelper.RecordPartialAssignerToCache(context, assigner);
                if (remainingQueueIds != null && remainingQueueIds.Count > 0)
                {
                    //requeue to continue
                    await EnqueueOptimizationContinueProcessAsync(context, remainingQueueIds, chargeType, skipLowerCostCheck);
                }
            }
            else
            {
                if (context.IsRedisConnectionStringValid && IsUsingRedisCache)
                {
                    RedisCacheHelper.ClearPartialAssignerFromCache(context, queueIds);
                }

                var isSuccess = assigner.Best_Result != null;
                if (isSuccess)
                {
                    // record results
                    var result = assigner.Best_Result;
                    if (amopCustomerId.HasValue)
                    {
                        RecordResults(context, result.QueueId, amopCustomerId.Value, commPlanGroupId, result, skipLowerCostCheck);
                    }
                    else
                    {
                        RecordResults(context, result.QueueId, accountNumber, commPlanGroupId, result, skipLowerCostCheck);
                    }
                }

                foreach (long queueId in queueIds)
                {
                    // stop queue
                    StopQueue(context, queueId, isSuccess);
                }
            }
        }

        public int? GetCustomerRatePoolsByCommGroupId(KeySysLambdaContext context, long commGroupId)
        {
            LogInfo(context, CommonConstants.SUB, $"({commGroupId})");
            var sqlRetryPolicy = new PolicyFactory(context.logger).GetSqlRetryPolicy(CommonConstants.NUMBER_OF_RETRIES);
            return sqlRetryPolicy.Execute(() =>
            {
                var parameters = new List<SqlParameter>()
                {
                    new SqlParameter(CommonSQLParameterNames.COMM_GROUP_ID, commGroupId),
                    new SqlParameter(CommonSQLParameterNames.CUSTOMER_RATE_POOL_ID_PASCAL_CASE, SqlDbType.Int)
                    {
                        Direction = ParameterDirection.Output
                    }
                };
                return SqlQueryHelper.ExecuteStoredProcedureWithSingleValueResult<int?>(ParameterizedLog(context), context.ConnectionString,
                    SQLConstant.StoredProcedureName.GET_CUSTOMER_RATE_POOLS_BY_COMM_GROUP_ID,
                    outputParamName: CommonSQLParameterNames.CUSTOMER_RATE_POOL_ID_PASCAL_CASE,
                    null,
                    parameters,
                    SQLConstant.ShortTimeoutSeconds);
            });
        }
    }
}
