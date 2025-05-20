using RAWSimO.Core.Configurations;
using RAWSimO.Core.Elements;
using RAWSimO.Core.IO;
using RAWSimO.Core.Items;
using RAWSimO.Core.Management;
using RAWSimO.Core.Metrics;
using RAWSimO.Toolbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static RAWSimO.Core.Control.BotManager;

namespace RAWSimO.Core.Control.Defaults.OrderBatching
{
    /// <summary>
    /// Implements a manager that uses information of the backlog to exploit similarities in orders when assigning them.
    /// </summary>
    public class HASManager : OrderManager
    {
        // --->>> BEST CANDIDATE HELPER FIELDS - USED FOR SELECTING THE NEXT BEST TASK
        /// <summary>
        /// The current pod to assess.
        /// </summary>
        private Pod _currentPod = null;
        /// <summary>
        /// The current output station to assess
        /// </summary>
        private OutputStation _currentOStation = null;
        /// <summary>
        /// 
        /// </summary>
        public double SumofNumberofDuetime = 0.0;
        /// <summary>
        /// 临时存储_pendingOrders1
        /// </summary>
        HashSet<Order> _pendingOrders1 = null;
        /// <summary>
        /// Creates a new instance of this manager.
        /// </summary>
        /// <param name="instance">The instance this manager belongs to.</param>
        public HASManager(Instance instance) : base(instance) { _config = instance.ControllerConfig.OrderBatchingConfig as HASConfiguration; }
        /// <summary>
        /// Stores the available counts per SKU for a pod for on-the-fly assessment.
        /// </summary>
        private VolatileIDDictionary<ItemDescription, int> _availableCounts;
        /// <summary>
        /// Initializes some fields for pod selection.
        /// </summary>
        private void InitPodSelection()
        {
            if (_availableCounts == null)
                _availableCounts = new VolatileIDDictionary<ItemDescription, int>(Instance.ItemDescriptions.Select(i => new VolatileKeyValuePair<ItemDescription, int>(i, 0)).ToList());
        }
        /// <summary>
        /// The config of this controller.
        /// </summary>
        private HASConfiguration _config;
        /// <summary>
        /// order进入Od的截止时间
        /// </summary>
        private double DueTimeOrderofMP = TimeSpan.FromMinutes(30).TotalSeconds;
        /// <summary>
        /// Checks whether another order is assignable to the given station.
        /// </summary>
        /// <param name="station">The station to check.</param>
        /// <returns><code>true</code> if there is another open slot, <code>false</code> otherwise.</returns>
        private bool IsAssignable(OutputStation station)
        { return station.Active && station.CapacityReserved + station.CapacityInUse < station.Capacity; }
        /// <summary>
        /// Checks whether another order is assignable to the given station.
        /// </summary>
        /// <param name="station">The station to check.</param>
        /// <returns><code>true</code> if there is another open slot and another one reserved for fast-lane, <code>false</code> otherwise.</returns>
        private bool IsAssignableKeepFastLaneSlot(OutputStation station)
        { return station.Active && station.CapacityReserved + station.CapacityInUse < station.Capacity; }

        private BestCandidateSelector _bestCandidateSelectNormal;
        private BestCandidateSelector _bestCandidateSelectFastLane;
        private Order _currentOrder = null;
        private VolatileIDDictionary<OutputStation, Pod> _nearestInboundPod;
        ///// <summary>
        /////临时分配给工作站的Pods
        ///// </summary>
        //Dictionary<OutputStation, HashSet<Pod>> _inboundPodsPerStation1 = null;
        /// <summary>
        /// Initializes this controller.
        /// </summary>
        private void Initialize()
        {
            // Set some values for statistics
            _statPodMatchingScoreIndex = _config.LateBeforeMatch ? 1 : 0;
            // --> Setup normal scorers
            List<Func<double>> normalScorers = new List<Func<double>>();
            // Select late orders first
            if (_config.LateBeforeMatch)
            {
                normalScorers.Add(() =>
                {
                    return _currentOrder.DueTime > Instance.Controller.CurrentTime ? 1 : 0;
                });
            }
            // Select best by match with inbound pods
            normalScorers.Add(() =>
            {
                return -_currentOrder.Timestay;
            });
            // If we run into ties use the oldest order
            normalScorers.Add(() =>
            {
                switch (_config.TieBreaker)
                {
                    case Shared.OrderSelectionTieBreaker.Random: return Instance.Randomizer.NextDouble();
                    case Shared.OrderSelectionTieBreaker.EarliestDueTime: return -_currentOrder.DueTime;
                    case Shared.OrderSelectionTieBreaker.FCFS: return -_currentOrder.TimeStamp;
                    default: throw new ArgumentException("Unknown tie breaker: " + _config.FastLaneTieBreaker);
                }
            });
            // --> Setup fast lane scorers
            List<Func<double>> fastLaneScorers = new List<Func<double>>();
            // If we run into ties use the oldest order
            fastLaneScorers.Add(() =>
            {
                switch (_config.FastLaneTieBreaker)
                {
                    case Shared.FastLaneTieBreaker.Random: return Instance.Randomizer.NextDouble();
                    case Shared.FastLaneTieBreaker.EarliestDueTime: return -_currentOrder.DueTime;
                    case Shared.FastLaneTieBreaker.FCFS: return -_currentOrder.TimeStamp;
                    default: throw new ArgumentException("Unknown tie breaker: " + _config.FastLaneTieBreaker);
                }
            });
            // Init selectors
            _bestCandidateSelectNormal = new BestCandidateSelector(true, normalScorers.ToArray());
            _bestCandidateSelectFastLane = new BestCandidateSelector(true, fastLaneScorers.ToArray());
            if (_config.FastLane)
                _nearestInboundPod = new VolatileIDDictionary<OutputStation, Pod>(Instance.OutputStations.Select(s => new VolatileKeyValuePair<OutputStation, Pod>(s, null)).ToList());
        }
        /// <summary>
        /// Determines a score that can be used to decide about an assignment.
        /// </summary>
        /// <returns>A score that can be used to decide about the best assignment. Minimization / Smaller is better.</returns>
        public double Score()
        {
            // Check picks leading to completed orders
            int completeableAssignedOrders = 0;
            //int NumberofItem = 0;
            Dictionary<ItemDescription, int> _availableCounts1 = new Dictionary<ItemDescription, int>();
            // Get current pod content
            foreach (var pod in _inboundPodsPerStation[_currentOStation])
            {
                foreach (var item in pod.ItemDescriptionsContained)
                {
                    if (_availableCounts1.ContainsKey(item))
                        _availableCounts1[item] += pod.CountAvailable(item);
                    else
                        _availableCounts1.Add(item, pod.CountAvailable(item));
                }
            }
            // Check all assigned orders
            SumofNumberofDuetime = 0.0;
            foreach (var order in _pendingOrders)
            {
                // Get demand for items caused by order
                List<IGrouping<ItemDescription, ExtractRequest>> itemDemands = Instance.ResourceManager.GetExtractRequestsOfOrder(order).GroupBy(r => r.Item).ToList();
                // Check whether sufficient inventory is still available in the pod (also make sure it is was available in the beginning, not all values were updated at the beginning of this function / see above)
                if (itemDemands.All(g => _inboundPodsPerStation[_currentOStation].Any(v => v.IsAvailable(g.Key)) && _availableCounts1[g.Key] >= g.Count()))
                {
                    // Update remaining pod content
                    foreach (var itemDemand in itemDemands)
                    {
                        _availableCounts1[itemDemand.Key] -= itemDemand.Count();
                        //NumberofItem += itemDemand.Count();
                    }
                    // Update number of completeable orders
                    completeableAssignedOrders++;
                    SumofNumberofDuetime += order.sequence;
                }
            }
            return -(100 * completeableAssignedOrders);
        }
        /// <summary>
        /// Determines a score that can be used to decide about an assignment.
        /// </summary>
        /// <param name="config">The config specifying further parameters.</param>
        /// <param name="pod">The pod.</param>
        /// <param name="station">The station.</param>
        /// <returns>A score that can be used to decide about the best assignment. Minimization / Smaller is better.</returns>
        public double Score(PCScorerPodForOStationBotDemand config, Pod pod, OutputStation station)
        {
            return -pod.ItemDescriptionsContained.Sum(i =>
                Math.Min(
                    // Overall demand
                    Instance.ResourceManager.GetDemandAssigned(i) + Instance.ResourceManager.GetDemandQueued(i) + Instance.ResourceManager.GetDemandBacklog(i),
                    // Stock offered by pod
                    pod.CountContained(i)));
        }
        internal bool AnyRelevantRequests(Pod pod)
        {
            return _pendingOrders.Any(o => Instance.ResourceManager.GetExtractRequestsOfOrder(o).Any(r => pod.IsAvailable(r.Item)) && Instance.
            ResourceManager.GetExtractRequestsOfOrder(o).GroupBy(r => r.Item).All(i => pod.CountAvailable(i.Key) >= i.Count()));
        }
        internal bool AnyRelevantRequests1(Pod pod)
        {
            return _pendingOrders.Any(o => Instance.ResourceManager.GetExtractRequestsOfOrder(o).Any(r => pod.IsAvailable(r.Item)));
        }
        /// <summary>
        /// 产生Od
        /// </summary>
        /// <param name="pendingOrders"></param>
        /// <returns></returns>
        public HashSet<Order> GenerateOd(HashSet<Order> pendingOrders)
        {
            foreach (Order order in pendingOrders)
                order.Timestay = order.DueTime - (Instance.SettingConfig.StartTime.AddSeconds(Convert.ToInt32(Instance.Controller.CurrentTime)) - order.TimePlaced).TotalSeconds;
            int i = 0;
            foreach (Order order in pendingOrders.OrderBy(v => v.Timestay).ThenBy(u => u.DueTime)) //先选剩余的截止时间最短的，再选开始时间最早的
            {
                order.sequence = i;
                i++;
            }
            HashSet<Order> Od = new HashSet<Order>();
            foreach (var order in pendingOrders.Where(v => v.Positions.Sum(line => Math.Min(Instance.ResourceManager.UnusedPods.Sum(pod => pod.CountAvailable(line.Key)), line.Value))
            == v.Positions.Sum(s => s.Value)))//保证Od中的所有order必须能被执行
            {
                if (order.Timestay < DueTimeOrderofMP)
                    Od.Add(order);
            }
            return Od;
        }
        /// <summary>
        /// Instantiates a scoring function from the given config.
        /// </summary>
        /// <param name="scorerConfig">The config.</param>
        /// <returns>The scoring function.</returns>
        private Func<double> GenerateScorerPodForOStationBot(PCScorerPodForOStationBot scorerConfig)
        {
            switch (scorerConfig.Type())
            {
                case PrefPodForOStationBot.Demand:
                    { PCScorerPodForOStationBotDemand tempcfg = scorerConfig as PCScorerPodForOStationBotDemand; return () => { return Score(tempcfg, _currentPod, _currentOStation); }; }
                case PrefPodForOStationBot.Completeable:
                    { PCScorerPodForOStationBotCompleteable tempcfg = scorerConfig as PCScorerPodForOStationBotCompleteable; return () => { return Score(); }; }
                case PrefPodForOStationBot.WorkAmount:
                    { PCScorerPodForOStationBotWorkAmount tempcfg = scorerConfig as PCScorerPodForOStationBotWorkAmount; return () => { return SumofNumberofDuetime; }; }
                default: throw new ArgumentException("Unknown score type: " + scorerConfig.Type());
            }
        }

        /// <summary>
        /// 产生Ns
        /// </summary>
        /// <returns></returns>
        public Dictionary<OutputStation, Dictionary<Pod, int>> GenerateNs()
        {
            Dictionary<OutputStation, Dictionary<Pod, int>> Ns = new Dictionary<OutputStation, Dictionary<Pod, int>>();
            //初始化Ns
            foreach (var station in Instance.OutputStations)
            {
                Dictionary<Pod, int> sequenceofpod = new Dictionary<Pod, int>();
                Ns.Add(station, sequenceofpod);
            }
            foreach (var station in Instance.OutputStations)
            {
                Dictionary<Pod, int> sequuenceofpod = new Dictionary<Pod, int>();
                IEnumerable<Pod> inboundPodsofstation = station.InboundPods;
                Dictionary<Pod, double> distenceofpod = new Dictionary<Pod, double>();
                Dictionary<Pod, double> distenceofpod1 = new Dictionary<Pod, double>();
                //已经分配而未完成拣选的pod  1 计算已经进入工作站缓冲区的pod
                foreach (Pod pod in inboundPodsofstation.Where(v => v.Bot != null && (station.Queues[station.Waypoint].Contains(v.Bot.CurrentWaypoint) || v.Bot.CurrentWaypoint == station.Waypoint)))
                    distenceofpod1.Add(pod, Distances.CalculateShortestPathPodSafe(pod.Bot.CurrentWaypoint, station.Waypoint, Instance));
                //已经分配而未完成拣选的pod  3 将已经分配而在工作站缓冲区外的的pod加入
                foreach (Pod pod in inboundPodsofstation.Where(v => !distenceofpod1.ContainsKey(v)))
                    distenceofpod.Add(pod, Distances.CalculateShortestPathPodSafe(pod.Waypoint == null ? pod.Bot.CurrentWaypoint : pod.Waypoint, station.Waypoint, Instance));

                int i = 0;
                foreach (var pod in distenceofpod1.OrderBy(v => v.Value).Select(v => v.Key))
                {
                    pod.sequence = i;
                    Ns[station].Add(pod, i);
                    i++;
                }
                foreach (var pod in distenceofpod.OrderBy(v => v.Value).Select(v => v.Key))
                {
                    pod.sequence = i;
                    Ns[station].Add(pod, i);
                    i++;
                }
            }
            return Ns;
        }

        /// <summary>
        /// Returns a list of relevant items for the given pod / output-station combination.
        /// </summary>
        /// <param name="pod">The pod in focus.</param>
        /// <param name="itemDemands">The station in focus.</param>
        /// <returns>A list of tuples of items to serve the respective extract-requests.</returns>
        internal List<ExtractRequest> GetPossibleRequests(Pod pod, IEnumerable<ExtractRequest> itemDemands)
        {
            // Init, if necessary
            InitPodSelection();
            // Match fitting items with requests
            List<ExtractRequest> requestsToHandle = new List<ExtractRequest>();
            // Get current content of the pod
            foreach (var item in itemDemands.Select(r => r.Item).Distinct())
                _availableCounts[item] = pod.CountAvailable(item);
            // First handle requests already assigned to the station
            foreach (var itemRequestGroup in itemDemands.GroupBy(r => r.Item))
            {
                // Handle as many requests as possible with the given SKU
                IEnumerable<ExtractRequest> possibleRequests = itemRequestGroup.Take(_availableCounts[itemRequestGroup.Key]);
                requestsToHandle.AddRange(possibleRequests);
                // Update content available in pod for the given SKU
                _availableCounts[itemRequestGroup.Key] -= possibleRequests.Count();
            }
            // Return the result
            return requestsToHandle;
        }
        /// <summary>
        /// 候选pod
        /// </summary> 
        private BestCandidateSelector _bestPodOStationCandidateSelector = null;
        /// <summary>
        /// POA and PPS
        /// </summary>
        /// <param name="validStationNormalAssignment"></param>
        /// <param name="CsOd"></param>
        public void HeuristicsPOAandPPS(Func<OutputStation, bool> validStationNormalAssignment, bool CsOd)
        {
            // Assign orders while possible
            bool furtherOptions = true;
            while (furtherOptions)
            {
                // Prepare helpers
                HashSet<Pod> SelectedPod = new HashSet<Pod>();
                OutputStation chosenStation = null;
                // Look for next station to assign orders to
                foreach (var station in Instance.OutputStations
                    // Station has to be valid
                    .Where(s => validStationNormalAssignment(s)))
                {
                    _currentOStation = station;
                L:
                    //进行POA操作
                    bool furtherOptions1 = true;
                    while (validStationNormalAssignment(station) && furtherOptions1)
                    {
                        _bestCandidateSelectNormal.Recycle();
                        Order chosenOrder = null;
                        // Search for best order for the station in all fulfillable orders        选择的订单必须满足库存的数量约束
                        foreach (var order in _pendingOrders.Where(o => o.Positions.All(p => _inboundPodsPerStation[_currentOStation].Sum(pod => pod.CountAvailable(p.Key)) >= p.Value)))
                        {
                            // Set order
                            _currentOrder = order;
                            // --> Assess combination    可以建立一个集合
                            if (_bestCandidateSelectNormal.Reassess())  //选出可以由当前inbound中的pod分拣的完整order，tie-breaker是order的item的数量
                            {
                                chosenStation = _currentOStation;
                                chosenOrder = _currentOrder;
                            }
                        }
                        // Assign best order if available
                        if (chosenOrder != null)
                        {
                            // Assign the order
                            AllocateOrder(chosenOrder, chosenStation);
                            _pendingOrders1.Remove(chosenOrder);
                            //对pod进行排序
                            Dictionary<OutputStation, Dictionary<Pod, int>> Ns = GenerateNs();
                            //对pod中的item进行标记
                            // Match fitting items with requests
                            List<ExtractRequest> requestsToHandleofAll = new List<ExtractRequest>();
                            IEnumerable<ExtractRequest> itemDemands = Instance.ResourceManager.GetExtractRequestsOfOrder(chosenOrder);
                            // Get current content of the pod
                            foreach (var pod in Ns[chosenStation].OrderBy(v => v.Value).Select(s => s.Key))
                            {
                                if (itemDemands.Where(v => !requestsToHandleofAll.Contains(v)).Any(g => pod.IsAvailable(g.Item)))
                                {
                                    // Get all fitting requests
                                    List<ExtractRequest> fittingRequests = GetPossibleRequests(pod, itemDemands.Where(v => !requestsToHandleofAll.Contains(v)));
                                    requestsToHandleofAll.AddRange(fittingRequests);
                                    // Update remaining pod content
                                    foreach (var fittingRequest in fittingRequests)
                                        pod.JustRegisterItem(fittingRequest.Item); //将pod中选中的item进行标记                                                                                  
                                    if (Instance.ResourceManager._Ziops1[chosenStation].ContainsKey(pod))
                                        Instance.ResourceManager._Ziops1[chosenStation][pod].AddRange(fittingRequests);
                                    else
                                        Instance.ResourceManager._Ziops1[chosenStation].Add(pod, fittingRequests);
                                }
                            }
                        }
                        else
                            furtherOptions1 = false;
                    }
                    //进行PPS操作
                    if (validStationNormalAssignment(station) && (CsOd || _pendingOrders.Count() > 0))
                    {
                        _bestPodOStationCandidateSelector.Recycle();
                        HashSet<Pod> BestPods = new HashSet<Pod>();
                        Pod[] arr = new Pod[Instance.ResourceManager.UnusedPods.Where(p => AnyRelevantRequests1(p) && !SelectedPod.Contains(p)).Count()];
                        int j = 0;
                        foreach (Pod pod in Instance.ResourceManager.UnusedPods.Where(p => AnyRelevantRequests1(p) && !SelectedPod.Contains(p)))
                        {
                            arr[j] = pod;
                            j++;
                        }
                        for (int i = 1; i < arr.Length + 1; i++)
                        {
                            //求组合
                            List<Pod[]> lst_Combination = FindPodSet<Pod>.GetCombination(arr, i); //得到所有的货架组合
                            foreach (var pods in lst_Combination)
                            {
                                foreach (var pod in pods)
                                    _inboundPodsPerStation[station].Add(pod);
                                if (_bestPodOStationCandidateSelector.Reassess())
                                {
                                    foreach (var pod in pods)
                                        BestPods.Add(pod);
                                }
                                foreach (var pod in pods)
                                    _inboundPodsPerStation[station].Remove(pod);

                            }
                            if (BestPods.Count() > 0)
                                break;
                            else
                                continue;
                        }
                        foreach (var pod in BestPods)
                        {
                            _inboundPodsPerStation[station].Add(pod);
                            SelectedPod.Add(pod);
                        }
                        goto L;
                    }
                }
                furtherOptions = false;
            }
        }
        /// <summary>
        /// This is called to decide about potentially pending orders.
        /// This method is being timed for statistical purposes and is also ONLY called when <code>SituationInvestigated</code> is <code>false</code>.
        /// Hence, set the field accordingly to react on events not tracked by this outer skeleton.
        /// </summary>
        protected override void DecideAboutPendingOrders()
        {
            // If not initialized, do it now
            if (_bestCandidateSelectNormal == null)
                Initialize();
            // Init
            InitPodSelection();
            foreach (var oStation in Instance.OutputStations)
                _inboundPodsPerStation[oStation] = new HashSet<Pod>(oStation.InboundPods);
            if (_bestPodOStationCandidateSelector == null)
            {
                _bestPodOStationCandidateSelector = new BestCandidateSelector(false,
                    GenerateScorerPodForOStationBot(_config1.PodSelectionConfig.OutputPodScorer),
                    GenerateScorerPodForOStationBot(_config1.PodSelectionConfig.OutputPodScorerTieBreaker1));
            }
            // Define filter functions
            Func<OutputStation, bool> validStationNormalAssignment = _config.FastLane ? (Func<OutputStation, bool>)IsAssignableKeepFastLaneSlot : IsAssignable;
            Func<OutputStation, bool> validStationFastLaneAssignment = IsAssignable;
            //Od为快到期order的集合
            HashSet<Order> Od = GenerateOd(_pendingOrders);
            //用于存储_pendingOrders的临时数据
            _pendingOrders1 = new HashSet<Order>(_pendingOrders);
            // Assign fast lane orders while possible
            if (Od.Count == 0)
                HeuristicsPOAandPPS(validStationNormalAssignment, true);
            else if (Cs.Sum(v => v.Value) > Od.Count && Od.Count > 0)
            {
                _pendingOrders = Od;
                HeuristicsPOAandPPS(validStationNormalAssignment, false);
                //DeletePod();
                _pendingOrders = _pendingOrders1;
                HeuristicsPOAandPPS(validStationNormalAssignment, true);
            }
            else if (Cs.Sum(v => v.Value) <= Od.Count && Od.Count > 0)
            {
                _pendingOrders = Od;
                HeuristicsPOAandPPS(validStationNormalAssignment, true);
                //DeletePod();
                _pendingOrders = _pendingOrders1;
            }
        }

        #region IOptimize Members

        /// <summary>
        /// Signals the current time to the mechanism. The mechanism can decide to block the simulation thread in order consume remaining real-time.
        /// </summary>
        /// <param name="currentTime">The current simulation time.</param>
        public override void SignalCurrentTime(double currentTime) { /* Ignore since this simple manager is always ready. */ }

        #endregion

        #region Custom stat tracking

        /// <summary>
        /// Contains the aggregated scorer values.
        /// </summary>
        private double[] _statScorerValues = null;
        /// <summary>
        /// Contains the number of assignments done.
        /// </summary>
        private double _statAssignments = 0;
        /// <summary>
        /// The index of the pod matching scorer.
        /// </summary>
        private int _statPodMatchingScoreIndex = -1;
        /// <summary>
        /// The callback indicates a reset of the statistics.
        /// </summary>
        public override void StatReset()
        {
            _statScorerValues = null;
            _statAssignments = 0;
        }
        /// <summary>
        /// The callback that indicates that the simulation is finished and statistics have to submitted to the instance.
        /// </summary>
        public override void StatFinish()
        {
            Instance.StatCustomControllerInfo.CustomLogOBString =
                _statScorerValues == null ? "" :
                string.Join(IOConstants.DELIMITER_CUSTOM_CONTROLLER_FOOTPRINT.ToString(), _statScorerValues.Select(e => e / _statAssignments).Select(e => e.ToString(IOConstants.FORMATTER)));
        }

        #endregion
    }
}

