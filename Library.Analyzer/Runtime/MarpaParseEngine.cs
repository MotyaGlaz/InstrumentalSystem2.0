﻿using Library.Analyzer.Charts;
using Library.Analyzer.Grammars;
using Library.Analyzer.Collections;
using System.Collections.Generic;
using Library.Analyzer.Tokens;
using System;
using System.Runtime.CompilerServices;
using Library.Analyzer.Forest;

namespace Library.Analyzer.Runtime
{
    public class MarpaParseEngine : IParseEngine
    {
        private PreComputedGrammar _preComputedGrammar;

        public DeterministicChart Chart { get; private set; }
        
        public int Location { get; private set; }

        public IGrammar Grammar => _preComputedGrammar.Grammar;

        public MarpaParseEngine(PreComputedGrammar preComputedGrammar)
        {
            _preComputedGrammar = preComputedGrammar;
            Chart = new DeterministicChart();
            Initialize();
        }

        public MarpaParseEngine(IGrammar grammar)
            : this(new PreComputedGrammar(grammar))
        { }

        private void Initialize()
        {
            var start = _preComputedGrammar.Start;
            AddEimPair(0, start, 0);
        }

        public void Reset()
        {
            Initialize();
        }

        public IInternalForestNode GetParseForestRootNode()
        {
            throw new NotImplementedException();
        }
                
        private static readonly ILexerRule[] EmptyLexerRules = { };
        private List<ILexerRule> _expectedLexerRules = new List<ILexerRule>();

        public IReadOnlyList<ILexerRule> GetExpectedLexerRules()
        {
            var deterministicSets = Chart.Sets;
            var deterministicSetCount = deterministicSets.Count;

            if (deterministicSetCount == 0)
                return EmptyLexerRules;

            _expectedLexerRules.Clear();

            var frameSet = deterministicSets[deterministicSets.Count - 1];
            for (var i = 0; i < frameSet.States.Count; i++)
            {
                var stateFrame = frameSet.States[i];
                for (int j = 0; j < stateFrame.DottedRuleSet.ScanKeys.Count; j++)
                {
                    var lexerRule = stateFrame.DottedRuleSet.ScanKeys[j];
                    var index = Grammar.GetLexerRuleIndex(lexerRule);
                    if (index < 0)
                        continue;
                    _expectedLexerRules.Add(lexerRule);
                }
            }
            return _expectedLexerRules;
        }
        
        public bool Pulse(IToken token)
        {
            ScanPass(Location, token);
            var tokenRecognized = Chart.Sets.Count > Location + 1;
            if (!tokenRecognized)
                return false;
            Location++;
            ReductionPass(Location);
            return true;
        }

        public bool Pulse(IReadOnlyList<IToken> tokens)
        {
            for (var i = 0; i < tokens.Count; i++)
                ScanPass(Location, tokens[i]);
            var tokenRecognized = Chart.Sets.Count > Location + 1;
            if (!tokenRecognized)
                return false;
            Location++;
            ReductionPass(Location);
            return true;
        }

        public bool Errors(IReadOnlyList<IToken> tokens)
        {
            throw new NotSupportedException("Error recovery is not supported");
        }

        public bool IsAccepted()
        {
            var anyEarleySets = Chart.Sets.Count > 0;
            if (!anyEarleySets)
                return false;

            var lastDeterministicSetIndex = Chart.Sets.Count - 1;
            var lastDeterministicSet = Chart.Sets[lastDeterministicSetIndex];

            return AnyDeterministicStateAccepted(lastDeterministicSet);
        }

        private bool AnyDeterministicStateAccepted(DeterministicSet lastFrameSet)
        {
            var lastDeterministicStateCount = lastFrameSet.States.Count;
            for (var i = 0; i < lastDeterministicStateCount; i++)
            {
                var deterministicState = lastFrameSet.States[i];
                var originIsFirstEarleySet = deterministicState.Origin == 0;
                if (!originIsFirstEarleySet)
                    continue;

                if (AnyPreComputedStateAccepted(deterministicState.DottedRuleSet.Data))
                    return true;
            }

            return false;
        }

        private bool AnyPreComputedStateAccepted(IReadOnlyList<IDottedRule> states)
        {
            for (var j = 0; j < states.Count; j++)
            {
                var preComputedState = states[j];                
                if (!preComputedState.IsComplete)
                    continue;

                if (!IsStartState(preComputedState))
                    continue;

                return true;
            }
            return false;
        }

        private void ScanPass(int iLoc, IToken token)
        {
            var iES = Chart.Sets[iLoc];
            for (var i = 0; i < iES.States.Count; i++)
            {
                var workEIM = iES.States[i];
                var fromAH = workEIM.DottedRuleSet;
                var origLoc = workEIM.Origin;

                var toAH = Goto(fromAH, token);
                if (toAH is null)
                    continue;
                AddEimPair(iLoc + 1, toAH, origLoc);
            }
        }

        private HashSet<ISymbol> _reductionPassHashSet;

        private void ReductionPass(int iLoc)
        {
            var iES = Chart.Sets[iLoc];
            _reductionPassHashSet ??= new HashSet<ISymbol>();            
            for (var i = 0; i < iES.States.Count; i++)
            {
                var workEIM = iES.States[i];
                var workAH = workEIM.DottedRuleSet;
                var origLoc = workEIM.Origin;

                for (var j = 0; j < workAH.Data.Count; j++)
                {
                    var dottedRule = workAH.Data[j];
                    if (!dottedRule.IsComplete)
                        continue;

                    var lhsSym = dottedRule.Production.LeftHandSide;
                    if (!_reductionPassHashSet.Add(lhsSym))
                        continue;

                    ReduceOneLeftHandSide(iLoc, origLoc, lhsSym);
                }
                _reductionPassHashSet.Clear();
            }
            MemoizeTransitions(iLoc);
        }

        private void ReduceOneLeftHandSide(int iLoc, int origLoc, INonTerminal lhsSym)
        {
            var deterministicSet = Chart.Sets[origLoc];
            var transitionItem = deterministicSet.FindCachedDottedRuleSetTransition(lhsSym);
            if (transitionItem != null)
                LeoReductionOperation(iLoc, transitionItem);
            else
            {
                for (var i = 0; i < deterministicSet.States.Count; i++)
                {
                    var stateFrame = deterministicSet.States[i];
                    EarleyReductionOperation(iLoc, stateFrame, lhsSym);
                }
            }
        }

        private Dictionary<ISymbol, int> _cachedCount;
        private Dictionary<ISymbol, CachedDottedRuleSetTransition> _cachedTransitions;

        private void MemoizeTransitions(int iLoc)
        {
            var deterministicSet = Chart.Sets[iLoc];
            // leo eligibility needs to be cached before creating the cached transition
            // if the size of the list is != 1, do not enter the cached frame transition
            _cachedCount ??= new Dictionary<ISymbol, int>();
            _cachedTransitions ??= new Dictionary<ISymbol, CachedDottedRuleSetTransition>();

            for (var i = 0; i < deterministicSet.States.Count; i++)
            {
                var deterministicState = deterministicSet.States[i];
                var dottedRuleSet = deterministicState.DottedRuleSet;
                var dottedRuleSetData = dottedRuleSet.Data;
                var dottedRuleSetDataCount = dottedRuleSetData.Count;

                for (var j = 0; j < dottedRuleSetDataCount; j++)
                {
                    var preComputedState = dottedRuleSetData[j];
                    if (preComputedState.IsComplete)
                        continue;

                    var postDotSymbol = preComputedState.PostDotSymbol;
                    if (postDotSymbol.SymbolType != SymbolType.NonTerminal)
                        continue;

                    // leo eligibile items are right recursive directly or indirectly                    
                    if (!_preComputedGrammar.Grammar.IsRightRecursive(
                        preComputedState.Production.LeftHandSide))
                        continue;

                    // to determine if the item is leo unique, cache it here
                    if (!_cachedCount.TryGetValue(postDotSymbol, out int count))
                    {
                        _cachedCount[postDotSymbol] = 1;
                        _cachedTransitions[postDotSymbol] = CreateTopCachedItem(deterministicState, postDotSymbol);
                    }
                    else
                    {
                        _cachedCount[postDotSymbol] = count + 1;
                    }
                }
            }

            // add all memoized leo items to the frameSet
            foreach (var symbol in _cachedCount.Keys)
            {
                var count = _cachedCount[symbol];
                if (count != 1)
                    continue;
                deterministicSet.AddCachedTransition(_cachedTransitions[symbol]);
            }

            _cachedTransitions.Clear();
            _cachedCount.Clear();
        }

        private CachedDottedRuleSetTransition CreateTopCachedItem(
            DeterministicState stateFrame, 
            ISymbol postDotSymbol)
        {
            var origin = stateFrame.Origin;
            CachedDottedRuleSetTransition topCacheItem = null;
            // search for the top item in the leo chain
            while (true)
            {
                var originFrameSet = Chart.Sets[stateFrame.Origin];
                var nextCachedItem = originFrameSet.FindCachedDottedRuleSetTransition(postDotSymbol);
                if (nextCachedItem is null)
                    break;
                topCacheItem = nextCachedItem;
                if (origin == nextCachedItem.Origin)
                    break;
                origin = topCacheItem.Origin;
            }

            return new CachedDottedRuleSetTransition(
                postDotSymbol,
                stateFrame.DottedRuleSet,
                topCacheItem is null ? stateFrame.Origin : origin);
        }

        private void EarleyReductionOperation(int iLoc, DeterministicState fromEim, ISymbol transSym)
        {
            var fromAH = fromEim.DottedRuleSet;
            var originLoc = fromEim.Origin;

            var toAH = Goto(fromAH, transSym);
            if (toAH is null)
                return;

            AddEimPair(iLoc, toAH, originLoc);
        }

        private void LeoReductionOperation(int iLoc, CachedDottedRuleSetTransition fromLim)
        {
            var fromAH = fromLim.DottedRuleSet;
            var transSym = fromLim.Symbol;
            var originLoc = fromLim.Origin;

            var toAH = Goto(fromAH, transSym);
            if (toAH is null)
                return;

            AddEimPair(iLoc, toAH, originLoc);
        }

        private void AddEimPair(int iLoc, DottedRuleSet confirmedAH, int origLoc)
        {
            var confirmedEIM = new DeterministicState(confirmedAH, origLoc);
            var predictedAH = Goto(confirmedAH);
            Chart.Enqueue(iLoc, confirmedEIM);
            if (predictedAH is null)
                return;
            var predictedEIM = new DeterministicState(predictedAH, iLoc);
            Chart.Enqueue(iLoc, predictedEIM);
        }
                
        private static DottedRuleSet Goto(DottedRuleSet fromAH)
        {
            return fromAH.NullTransition;
        }

        private static DottedRuleSet Goto(DottedRuleSet fromAH, ISymbol symbol)
        {
            return fromAH.Reductions.GetOrReturnNull(symbol);         
        }

        private static DottedRuleSet Goto(DottedRuleSet fromAH, IToken token)
        {
            return fromAH.TokenTransitions.GetOrReturnNull(token.TokenType);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsStartState(IDottedRule state)
        {
            var start = _preComputedGrammar.Grammar.Start;
            return state.Production.LeftHandSide.Equals(start);
        }
    }
}
