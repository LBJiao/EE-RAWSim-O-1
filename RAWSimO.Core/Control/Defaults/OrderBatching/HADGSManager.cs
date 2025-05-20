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
using RAWSimO.SolverWrappers;
using static RAWSimO.Core.Management.ResourceManager;
using System.Threading;
using static RAWSimO.Core.Control.BotManager;
using System.IO;

namespace RAWSimO.Core.Control.Defaults.OrderBatching
{

    /// <summary>
    /// Implements a manager that uses information of the backlog to exploit similarities in orders when assigning them.
    /// </summary>
    public class HADGSManager : OrderManager
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
        Bot currentrobot = null;
        /// <summary>
        /// 邻域搜索的次数
        /// </summary>
        public int LocalSearch = 3;
        bool IfSimplePOAandPPS = false;
        /// <summary>
        /// 临时存储_pendingOrders1
        /// </summary>
        HashSet<Order> _pendingOrders1 = null;
        /// <summary>
        /// Creates a new instance of this manager.
        /// </summary>
        /// <param name="instance">The instance this manager belongs to.</param>
        public HADGSManager(Instance instance) : base(instance) { _config = instance.ControllerConfig.OrderBatchingConfig as HADGSConfiguration; }
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
        private HADGSConfiguration _config;
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
                HashSet<ExtractRequest> itemDemands = new HashSet<ExtractRequest>();
                foreach (var pod in Instance.ResourceManager.GetExtractRequestsOfOrder(_currentOrder))
                    itemDemands.Add(pod);
                HashSet<Pod> Pods = new HashSet<Pod>();
                // Get current content of the pod
                foreach (var pod in _inboundPodsPerStation[_currentOStation].OrderBy(v => DistanceSet[_currentOStation.Waypoint.ID][v.Waypoint == null ? v.Bot.CurrentWaypoint.ID : v.Waypoint.ID]))
                {
                    if (itemDemands.Any(g => pod.IsAvailable(g.Item)))
                    {
                        // Get all fitting requests
                        List<ExtractRequest> fittingRequests = GetPossibleRequests(pod, itemDemands);
                        //requestsToHandleofAll.AddRange(fittingRequests);
                        foreach (var fittingRequest in fittingRequests)
                            itemDemands.Remove(fittingRequest);
                        Pods.Add(pod);
                    }
                }
                return Pods.Sum(v => DistanceSet[_currentOStation.Waypoint.ID][v.Waypoint == null ? v.Bot.CurrentWaypoint.ID : v.Waypoint.ID]);
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
            if (completeableAssignedOrders == 0)
                return double.MaxValue;
            else
            {
                //return (CurrentPodtoBot.Sum(v => Distances.CalculateManhattan1(v.Value.CurrentWaypoint, v.Key.Waypoint)) / completeableAssignedOrders);
                return -(40 * completeableAssignedOrders) + CurrentPodtoBot.Sum(v => Distances.CalculateManhattan1(v.Value.CurrentWaypoint, v.Key.Waypoint) + DistanceSet[_currentOStation.Waypoint.ID][v.Key.Waypoint.ID]); //v.Key.Waypoint == null ? v.Bot.CurrentWaypoint.ID 
            }

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
        /// <summary>
        /// Prepares some meta information.
        /// </summary>
        private void PrepareAssessment()
        {
            if (_config.FastLane)
            {
                foreach (var station in Instance.OutputStations.Where(s => IsAssignable(s)))
                {
                    _nearestInboundPod[station] = station.InboundPods.ArgMin(p =>
                    {
                        if (p.Bot != null && p.Bot.CurrentWaypoint != null)
                            // Use the path distance (this should always be possible)
                            return Distances.CalculateShortestPathPodSafe(p.Bot.CurrentWaypoint, station.Waypoint, Instance);
                        else
                            // Use manhattan distance as a fallback
                            return Distances.CalculateManhattan(p, station, Instance.WrongTierPenaltyDistance);
                    });
                }
            }
        }
        internal bool AnyRelevantRequests(Pod pod)
        {
            return _pendingOrders.Any(o => Instance.ResourceManager.GetExtractRequestsOfOrder(o).Any(r => pod.IsAvailable(r.Item)) && Instance.
            ResourceManager.GetExtractRequestsOfOrder(o).GroupBy(r => r.Item).All(i => pod.CountAvailable(i.Key) >= i.Count()));
        }
        internal bool AnyRelevantRequests1(Pod pod)  //pod最少可以向_pendingOrders提供一个item
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
        /// 已经被选择的pod集合
        /// </summary>
        public HashSet<Pod> SelectedPod = new HashSet<Pod>();
        /// <summary>
        ///新分配的bot对应的pod
        /// </summary>
        public Dictionary<Pod, Bot> CurrentPodtoBot = new Dictionary<Pod, Bot>();
        ///// <summary>
        ///// 所有可能的组合
        ///// </summary>
        //public HashSet<HashSet<Pod>> FindSetofPod(int num)
        //{
        //    HashSet<HashSet<Pod>> Setpods = new HashSet<HashSet<Pod>>();
        //    foreach (var pod1 in Instance.ResourceManager.UnusedPods.Where(p => AnyRelevantRequests1(p) && !SelectedPod.Contains(p)))
        //    {
        //        _inboundPodsPerStation[station].Add(pod1);
        //        if (_bestPodOStationCandidateSelector.Reassess())
        //            bestPod = pod1;
        //        _inboundPodsPerStation[station].Remove(pod1);
        //    }
        //    return Setpods;
        //}
        /// <summary>
        /// POA and PPS
        /// </summary>
        /// <param name="validStationNormalAssignment"></param>
        public void HeuristicsPOAandPPS(Func<OutputStation, bool> validStationNormalAssignment)
        {
            // Assign orders while possible
            bool furtherOptions = true;
            while (furtherOptions)
            {
                // Prepare helpers
                SelectedPod = new HashSet<Pod>();
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
                        _bestCandidateSelectNormal.Recycle2();
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
                            //Dictionary<OutputStation, Dictionary<Pod, int>> Ns = GenerateNs();
                            //对pod中的item进行标记
                            // Match fitting items with requests
                            //List<ExtractRequest> requestsToHandleofAll = new List<ExtractRequest>();
                            HashSet<ExtractRequest> itemDemands = new HashSet < ExtractRequest >();
                            foreach (var pod in Instance.ResourceManager.GetExtractRequestsOfOrder(chosenOrder))
                                itemDemands.Add(pod);
                            //int i = 0;
                            // Get current content of the pod
                            foreach (var pod in _inboundPodsPerStation[chosenStation].OrderBy(v => DistanceSet[chosenStation.Waypoint.ID][v.Waypoint == null ? v.Bot.CurrentWaypoint.ID : v.Waypoint.ID]))
                            {
                                if (itemDemands.Any(g => pod.IsAvailable(g.Item)))
                                {
                                    // Get all fitting requests
                                    List<ExtractRequest> fittingRequests = GetPossibleRequests(pod, itemDemands);
                                    //requestsToHandleofAll.AddRange(fittingRequests);
                                    foreach (var fittingRequest in fittingRequests)
                                        itemDemands.Remove(fittingRequest);
                                    // Update remaining pod content
                                    foreach (var fittingRequest in fittingRequests)
                                    {
                                        pod.JustRegisterItem(fittingRequest.Item); //将pod中选中的item进行标记   
                                        //i++;
                                    }
                                    if (Instance.ResourceManager._Ziops1[chosenStation].ContainsKey(pod))
                                        Instance.ResourceManager._Ziops1[chosenStation][pod].AddRange(fittingRequests);
                                    else
                                        Instance.ResourceManager._Ziops1[chosenStation].Add(pod, fittingRequests);
                                    //foreach (var sta in Instance.OutputStations.Where(v => Instance.ResourceManager._Ziops1[v].Count > 0))
                                    //{
                                    //    foreach (var podtoziops in Instance.ResourceManager._Ziops1[sta].Where(v => !Instance.ResourceManager.BottoPod.ContainsValue(v.Key) && Instance.ResourceManager._usedPods.ContainsKey(v.Key)))
                                    //    {
                                    //        if (Instance.ResourceManager._usedPods[podtoziops.Key].CurrentTask is RestTask)
                                    //            Thread.Sleep(1);
                                    //    }
                                    //}
                                }
                            }
                            //if (i > 0 && 0 != itemDemands.Count())
                            //    throw new InvalidOperationException("Could not any request from the selected pod!");
                        }
                        else
                            furtherOptions1 = false;
                    }
                    HashSet<Bot> Ra = new HashSet<Bot>();
                    foreach (var bot in Instance._outputstationbots) //产生Ra
                    {
                        if (bot.Pod == null && !Instance.ResourceManager._usedPods.ContainsValue(bot) && !Instance.ResourceManager.BottoPod.ContainsKey(bot)) //
                            Ra.Add(bot);
                    }
                    if (Ra.Count == 0)
                        continue;
                    //进行PPS操作
                    if (validStationNormalAssignment(station) && _pendingOrders.Count() > 0)
                    {
                        _bestPodOStationCandidateSelector.Recycle();//初始化
                        Pod BestPod = null;
                        Bot BestRobot = null;
                        IfSimplePOAandPPS = false;
                        foreach (var pod in Instance.ResourceManager.UnusedPods.Where(p => AnyRelevantRequests1(p) && !SelectedPod.Contains(p) && !Instance.ResourceManager.BottoPod.ContainsValue(p))) //&& !Instance.ResourceManager.BottoPod.ContainsValue(p)
                        {
                            _inboundPodsPerStation[station].Add(pod);
                            CurrentPodtoBot.Clear();
                            _currentPod = pod;
                            currentrobot = Ra.OrderBy(v => Distances.CalculateManhattan1(v.CurrentWaypoint, pod.Waypoint)).FirstOrDefault();//找出距离pod最近的robot
                            CurrentPodtoBot.Add(_currentPod, currentrobot);
                            if (_bestPodOStationCandidateSelector.Reassess())
                            {
                                BestPod = pod;
                                BestRobot = currentrobot;
                            }
                            _inboundPodsPerStation[station].Remove(pod);
                        }
                        if (BestPod == null)
                        {
                            if (Ra.Count == 1)
                                continue;
                            IfSimplePOAandPPS = true;
                            SimplePOAandPPS(station, ref IfSimplePOAandPPS, Ra);//保证已分配的pod最少可以满足一个完整的order
                            if (!IfSimplePOAandPPS)
                                continue;
                        }
                        else
                        {
                            _inboundPodsPerStation[station].Add(BestPod);
                            SelectedPod.Add(BestPod);
                            station.RegisterInboundPod(BestPod);
                            Instance.ResourceManager.BottoPod.Add(BestRobot, BestPod);
                            Instance.ResourceManager.ClaimPod(BestPod, BestRobot, BotTaskType.Extract);
                        }
                        //foreach (var sta in Instance.OutputStations.Where(v => Instance.ResourceManager._Ziops1[v].Count > 0))
                        //{
                        //    foreach (var podtoziops in Instance.ResourceManager._Ziops1[sta].Where(v => !Instance.ResourceManager.BottoPod.ContainsValue(v.Key) && Instance.ResourceManager._usedPods.ContainsKey(v.Key)))
                        //    {
                        //        if (Instance.ResourceManager._usedPods[podtoziops.Key].CurrentTask is RestTask)
                        //            Thread.Sleep(1);
                        //    }
                        //}
                        goto L;
                    }
                }
                furtherOptions = false;
            }
        }
        /// <summary>
        /// 生成PiSKU
        /// </summary>
        /// <param name="order"></param>
        /// <param name="station"></param>
        /// <returns></returns>
        public List<List<HashSet<Pod>>> GeneratePiSKU(Order order, OutputStation station)
        {
            Dictionary<ItemDescription, HashSet<Pod>> piSKU = new Dictionary<ItemDescription, HashSet<Pod>>();
            List<List<HashSet<Pod>>> PiSKU = new List<List<HashSet<Pod>>>();
            HashSet<ItemDescription> setofitem = new HashSet<ItemDescription>();
            foreach (var item in order.Positions)
            {
                setofitem.Add(item.Key);
                foreach (var pod in Instance.ResourceManager.UnusedPods.Concat(_inboundPodsPerStation[station]).Where(v => v.IsAvailable(item.Key))) //&& !SelectedPod.Contains(v)
                {
                    if (piSKU.ContainsKey(item.Key))
                    {
                        if (!piSKU[item.Key].Contains(pod))
                            piSKU[item.Key].Add(pod);
                    }
                    else
                    {
                        HashSet<Pod> listofpod = new HashSet<Pod>() { pod };
                        piSKU.Add(item.Key, listofpod);
                    }
                }
                Pod[] arr = piSKU[item.Key].ToArray();
                List<HashSet<Pod>> listofpod1 = new List<HashSet<Pod>>();
                for (int i = 1; i < arr.Length + 1; i++)
                {
                    //求组合
                    List<Pod[]> lst_Combination = FindPodSet<Pod>.GetCombination(arr, i); //得到所有的货架组合
                    foreach (var podlist in lst_Combination.Where(v => v.Sum(p => p.CountAvailable(item.Key)) >= _availableCounts[item.Key]).OrderBy(s => s.Length))
                    {
                        HashSet<Pod> hspod = new HashSet<Pod>();
                        foreach (var pod in podlist)
                            hspod.Add(pod);
                        listofpod1.Add(hspod);
                    }
                    if (listofpod1.Count > 0)
                        break;
                }
                PiSKU.Add(listofpod1);
            }
            return PiSKU;
        }
        /// <summary>
        /// 用指派模型来求解任务分配问题
        /// </summary>
        /// <param name="type"></param>
        /// <param name="Pods"></param>
        /// <param name="station"></param>
        /// <param name="Ra"></param>
        public void SolveByMp(SolverType type, HashSet<Pod> Pods, OutputStation station, HashSet<Bot> Ra)
        {
            CurrentPodtoBot.Clear();
            LinearModel wrapper = new LinearModel(type, (string s) => { Console.Write(s); });
            List<Symbol> deVarNameyrp = new List<Symbol>();
            foreach (var robot in Ra)
            {
                foreach (var pod in Pods)
                    deVarNameyrp.Add(new Symbol { pod = pod, robot = robot, name = "yrp" + "_" + robot.ID.ToString() + "_" + pod.ID.ToString() });
            }
            VariableCollection<string> variablesBinary = new VariableCollection<string>(wrapper, VariableType.Binary, 0, 1, (string s) => { return s; });
            //目标函数为最小化bot到pod的距离之和
            wrapper.SetObjective(LinearExpression.Sum(deVarNameyrp.Select(v => variablesBinary[v.name] * Distances.CalculateManhattan1(v.robot.CurrentWaypoint, v.pod.Waypoint))), OptimizationSense.Minimize);
            foreach (var pod in Pods)//每个货架必须被一个机器人运输
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyrp.Where(v => v.pod.ID == pod.ID).Select(v => variablesBinary[v.name])) == 1, "shi1");
            foreach (var robot in Ra)//每个机器人最多只能分配给一个货架
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyrp.Where(v => v.robot.ID == robot.ID).Select(v => variablesBinary[v.name])) <= 1, "shi2");
            wrapper.Update();
            wrapper.Optimize();
            if (wrapper.HasSolution())
            {
                foreach (var itemName in deVarNameyrp)
                {
                    if (Math.Round(variablesBinary[itemName.name].GetValue()) != 0)
                    {
                        _inboundPodsPerStation[station].Add(itemName.pod);
                        CurrentPodtoBot.Add(itemName.pod, itemName.robot);
                    }
                }
            }
        }
        /// <summary>
        /// 根据确定的order来选择相应的pod
        /// </summary>
        /// <param name="station"></param>
        /// <param name="IfSimplePOAandPPS"></param>
        /// <param name="Ra"></param>
        public void SimplePOAandPPS(OutputStation station, ref bool IfSimplePOAandPPS, HashSet<Bot> Ra)
        {
            int cs = Cs[station];
            int LS = 0;
            LocalSearch = 3;
            HashSet<Order> pendingOrders = new HashSet<Order>(_pendingOrders.Where(o => o.Positions.All(p => Instance.ResourceManager.UnusedPods.Concat(_inboundPodsPerStation[station]).
            Sum(pod => pod.CountAvailable(p.Key)) >= p.Value)));
            if (pendingOrders.Count < LocalSearch)
                LocalSearch = pendingOrders.Count;
            HashSet<Order> SelectedOrders = new HashSet<Order>();
            if (cs > 0 && pendingOrders.Count > 0 && Ra.Count > 0)
            {
                Order bestorder = new Order();
                Dictionary<Pod, Bot> BestPodtoBot = new Dictionary<Pod, Bot>();
                L: Order order = pendingOrders.OrderBy(u => u.sequence).FirstOrDefault();  //选择优先级最高并且满足库存需求的一个order
                pendingOrders.Remove(order);
                foreach (var item in order.Positions)
                {
                    _availableCounts[item.Key] = item.Value;
                }
                List<List<HashSet<Pod>>> PiSKU = new List<List<HashSet<Pod>>>();
                PiSKU = GeneratePiSKU(order, station); //生成满足order中每个item需求的pod集合
                var result = PiSKU.Skip(1).Aggregate(PiSKU.First().StartCombo(), (serials, current) => serials.Combo(current), x => x).ToList();//生成满足order需求的pod集合
                //if (PiSKU.Select(v => v.Count).Aggregate((av, e) => av * e) != result.Count)
                //    throw new InvalidOperationException("Could not any request from the selected pod!");
                List<HashSet<Pod>> hashset = new List<HashSet<Pod>>();
                foreach (var sets in result)
                {
                    HashSet<Pod> hashgset = new HashSet<Pod>();
                    foreach (var set in sets)
                    {
                        foreach (var st in set.Where(v => !_inboundPodsPerStation[station].Contains(v)))//去掉已经分配给工作站的pod
                            hashgset.Add(st);
                    }
                    if (hashgset.Distinct().Count() <= Ra.Count())
                    {
                        HashSet<Pod> hashggset = new HashSet<Pod>();
                        foreach (var set in hashgset.Distinct())
                            hashggset.Add(set);
                       hashset.Add(hashggset);
                    }

                }
                if (LS == 0)
                    _bestPodOStationCandidateSelector.Recycle();//初始化
                int ii = 0;
                while (hashset.Count > 0 && ii < 10)
                {
                    Random rd = new Random();
                    int i = rd.Next(0, hashset.Count);
                    HashSet<Pod> pods = new HashSet<Pod>(hashset[i]);
                    hashset.RemoveAt(i);
                    SolveByMp(SolverType.Gurobi, pods, station, Ra);//用指派模型来求解任务分配问题
                   if (_bestPodOStationCandidateSelector.Reassess())
                    {
                        BestPodtoBot.Clear();
                        foreach (var podtobot in CurrentPodtoBot)
                        {
                            bestorder = order;
                            BestPodtoBot.Add(podtobot.Key, podtobot.Value);
                        }
                    }
                    foreach (var pod in pods)
                        _inboundPodsPerStation[station].Remove(pod);
                    ii++;
                }
                //while (hashset.Count > 0 && ii < 2)
                //{
                //    Random rd = new Random();
                //    int i = rd.Next(0, hashset.Count);
                //    HashSet<Pod> pods = new HashSet<Pod>(hashset[i]);
                //    hashset.RemoveAt(i);
                //    CurrentPodtoBot.Clear();
                //    foreach (var pod in pods)
                //    {
                //        _inboundPodsPerStation[station].Add(pod);
                //        currentrobot = Ra.OrderBy(v => Distances.CalculateManhattan1(v.CurrentWaypoint, pod.Waypoint)).FirstOrDefault();//找出距离pod最近的robot
                //        Ra.Remove(currentrobot);
                //        CurrentPodtoBot.Add(pod, currentrobot);
                //    }
                //    if (_bestPodOStationCandidateSelector.Reassess())
                //    {
                //        BestPodtoBot.Clear();
                //        foreach (var podtobot in CurrentPodtoBot)
                //        {
                //            bestorder = order;
                //            BestPodtoBot.Add(podtobot.Key, podtobot.Value);
                //        }
                //    }
                //    foreach (var pod in pods)
                //    {
                //        _inboundPodsPerStation[station].Remove(pod);
                //        Ra.Add(CurrentPodtoBot[pod]);
                //    }
                //    ii++;
                //}
                LS++;
                if (LS < LocalSearch)
                    goto L;
                if (BestPodtoBot.Count == 0)
                {
                    pendingOrders.Clear();
                    pendingOrders = new HashSet<Order>(_pendingOrders);
                    foreach (var o in SelectedOrders)
                        pendingOrders.Remove(o);
                    //if (hashset.OrderBy(v => v.Count()).FirstOrDefault().Count > 2 || Ra.Count > 2)
                    //{
                    //    while (pendingOrders.Count > 0)
                    //    {
                    //        Order o = pendingOrders.First();
                    //        _pendingOrders1.Remove(o);
                    //        _pendingOrders.Remove(o);
                    //        pendingOrders.Remove(o);
                    //        Instance.ItemManager.DeleteOrder(o);
                    //    }
                    //}
                    if (pendingOrders.Count == _pendingOrders.Count)
                        IfSimplePOAandPPS = false;
                    return;
                }
                else
                {
                    foreach (var podtobot in BestPodtoBot)
                    {
                        Ra.Remove(podtobot.Value);
                        _inboundPodsPerStation[station].Add(podtobot.Key);
                        SelectedPod.Add(podtobot.Key);
                        station.RegisterInboundPod(podtobot.Key);
                        Instance.ResourceManager.BottoPod.Add(podtobot.Value, podtobot.Key);
                        Instance.ResourceManager.ClaimPod(podtobot.Key, podtobot.Value, BotTaskType.Extract);
                    }
                }
                SelectedOrders.Add(bestorder);
                cs--;
                pendingOrders.Clear();
                pendingOrders = new HashSet<Order>(_pendingOrders);
                foreach (var o in SelectedOrders)
                    pendingOrders.Remove(o);
                LS = 0;
                if (pendingOrders.Count < LocalSearch)
                    LocalSearch = pendingOrders.Count;
            }
        }
        ///// <summary>
        ///// CopyHashSet
        ///// </summary>
        ///// <param name="copyHashSet"></param>
        ///// <param name="copyHashSetto"></param>
        ///// <returns></returns>
        //public HashSet<Order> CopyHashSet(HashSet<Order> copyHashSet, out HashSet<Order> copyHashSetto)
        //{
        //    foreach (Order order in copyHashSet)
        //        copyHashSetto.Add(order);
        //}
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
            _inboundPodsPerStation.Clear();
            foreach (var oStation in Instance.OutputStations)
            {
                _inboundPodsPerStation[oStation] = new HashSet<Pod>(oStation.InboundPods);
                foreach (var pod in oStation.InboundPods)
                {
                    if (!Instance.ResourceManager.BottoPod.ContainsValue(pod) && Instance.ResourceManager._usedPods[pod].CurrentTask is RestTask)
                    {
                        _inboundPodsPerStation[oStation].Remove(pod);
                        Instance.ResourceManager.ReleasePod(pod);
                        oStation.UnregisterInboundPod(pod);
                        break;
                    }
                }
            }
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
            if (Od.Count == 0 || Od.Count < Cs.Sum(v => v.Value))
                HeuristicsPOAandPPS(validStationNormalAssignment);
            else
            {
                _pendingOrders.Clear();
                _pendingOrders = new HashSet<Order>(Od);
                HeuristicsPOAandPPS(validStationNormalAssignment);
                _pendingOrders.Clear();
                _pendingOrders = new HashSet<Order>(_pendingOrders1);
            }
            //foreach (var sta in Instance.OutputStations.Where(v => Instance.ResourceManager._Ziops1[v].Count > 0))
            //{
            //    foreach (var podtoziops in Instance.ResourceManager._Ziops1[sta].Where(v => !Instance.ResourceManager.BottoPod.ContainsValue(v.Key) && Instance.ResourceManager._usedPods.ContainsKey(v.Key)))
            //    {
            //        if (Instance.ResourceManager._usedPods[podtoziops.Key].CurrentTask is RestTask)
            //            Thread.Sleep(1);

            //    }
            //}

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
