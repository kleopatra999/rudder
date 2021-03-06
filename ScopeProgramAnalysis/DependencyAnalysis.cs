﻿using System.Collections.Generic;
using Model;
using Model.Types;
using Backend.Analyses;
using Backend.Serialization;
using Backend.Model;
using Backend.Utils;
using Model.ThreeAddressCode.Instructions;
using Model.ThreeAddressCode.Values;
using Model.ThreeAddressCode.Expressions;
using System.Linq;

namespace ScopeProgramAnalysis
{

    class ScopeAnalysisConstants
    {
        public const string SCOPE_ROW_ENUMERATOR_METHOD = "System.Collections.Generic.IEnumerable<ScopeRuntime.Row>.GetEnumerator";
    }
    public enum ProtectedRowKind { Unknown, Input, Output };
    public class ProtectedRowNode : ParameterNode
    {
        public ProtectedRowKind RowKind { get; private set; }

        public ProtectedRowNode(ParameterNode n, ProtectedRowKind kind) : base(n.Id, n.Parameter, n.Type)
        {
            this.RowKind = kind;
        }
        public static ProtectedRowKind GetKind(IType type)
        {
            var result = ProtectedRowKind.Unknown;
            if(type.Equals(ScopeTypes.Row))
            {
                result = ProtectedRowKind.Output;
            }
            else if (type.Equals(ScopeTypes.RowSet))
            {
                result = ProtectedRowKind.Input;
            }
            return result;
        }
        public override string ToString()
        {
            var kind = KindToString();
            return kind + ":" + base.ToString();
        }

        public string KindToString()
        {
            string kind = "";
            switch (RowKind)
            {
                case ProtectedRowKind.Input:
                    kind = "Input";
                    break;
                case ProtectedRowKind.Output:
                    kind = "Output";
                    break;
                default:
                    kind = "Unkown";
                    break;
            }
            return kind;
        }
        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }
        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }

    class SongTaoDependencyAnalysis
    {
        private Host host;
        private IteratorPointsToAnalysis pointsToAnalyzer;
        private MethodDefinition moveNextMethod;
        private IDictionary<IVariable, IExpression> equalities;
        private MethodDefinition entryMethod;
        // private IDictionary<string,IVariable> specialFields;
        private MethodDefinition getEnumMethod;
        private InterproceduralManager interprocManager;

        public ISet<TraceableColumn> InputColumns { get; private set; }
        public ISet<TraceableColumn> OutputColumns { get; private set; }


        public SongTaoDependencyAnalysis(Host host,
                                        InterproceduralManager interprocManager,
                                        MethodDefinition method,
                                        MethodDefinition entryMethod,
                                        MethodDefinition getEnumMethod)
        {
            this.interprocManager = interprocManager;
            this.host = host;
            this.moveNextMethod = method;
            this.entryMethod = entryMethod;
            this.getEnumMethod = getEnumMethod;
            this.equalities = new Dictionary<IVariable, IExpression>();
        }

        public DependencyPTGDomain AnalyzeMoveNextMethod()
        {
            // 1) Analyze the entry method that creates, populates  and return the clousure 
            var cfgEntry = entryMethod.DoAnalysisPhases(host);
            var pointsToEntry = new IteratorPointsToAnalysis(cfgEntry, this.entryMethod); // , this.specialFields);
            var entryResult = pointsToEntry.Analyze();
            var ptgOfEntry = entryResult[cfgEntry.Exit.Id].Output;

            // 2) Call the GetEnumerator that may create a new clousure and polulate it
            var myGetEnumResult = new LocalVariable("$_temp_it") { Type = getEnumMethod.ReturnType };
            ptgOfEntry.Add(myGetEnumResult);
            var ptgAfterEnum = this.interprocManager.PTAInterProcAnalysis(ptgOfEntry, new List<IVariable> { pointsToEntry.ReturnVariable }, myGetEnumResult, this.getEnumMethod);

            // These are the nodes that we want to protect/analyze
            var protectedNodes = ptgOfEntry.Nodes.OfType<ParameterNode>()
                                 .Where(n => IsScopeType(n.Type)).Select(n => new ProtectedRowNode(n, ProtectedRowNode.GetKind(n.Type)));

            // I no longer need this. 
            //var specialFields = cfgEntry.ForwardOrder[1].Instructions.OfType<StoreInstruction>()
            //    .Where(st => st.Result is InstanceFieldAccess).Select(st => new KeyValuePair<string,IVariable>((st.Result as InstanceFieldAccess).FieldName,st.Operand) );
            //this.specialFields = specialFields.ToDictionary(item => item.Key, item => item.Value);


            // 3) I bing the current PTG with the parameters of MoveNext method on the clousure
            
            // Well... Inlining is broken we we added the Exceptional control graph. Let's avoid it
            //var cfg = this.moveNextMethod.DoAnalysisPhases(host, this.GetMethodsToInline());

            var cfg = this.interprocManager.GetCFG(this.moveNextMethod);
            PropagateExpressions(cfg, this.equalities);
            // In general, the variable to bind is going to be pointsToEntry.ReturnVariable which is aliased with "$_temp_it" (myGetEnumResult)
            SimplePointsToGraph calleePTG = InterproceduralManager.PTABindCallerCallee(ptgAfterEnum, new List<IVariable> { myGetEnumResult }, this.moveNextMethod);
            this.pointsToAnalyzer = new IteratorPointsToAnalysis(cfg, this.moveNextMethod, calleePTG);
                        
            //this.pta= this.interprocManager.PTABindAndRunInterProcAnalysis(ptgAfterEnum, new List<IVariable> { myGetEnumResult }, this.moveNextMethod, cfg);

            //var pointsTo = new IteratorPointsToAnalysis(cfg, this.moveNextMethod, this.specialFields);
            //this.ptAnalysisResult = pointsTo.Analyze();

            // var pointsTo = new IteratorPointsToAnalysis(cfg, this.moveNextMethod, this.specialFields, ptgOfEntry);

            // Now I analyze the Movenext method with the proper initialization 
            var result = this.AnalyzeScopeMethod(cfg, pointsToAnalyzer, protectedNodes);

            
            return result;
        }

        private IEnumerable<IMethodReference> GetMethodsToInline()
        {
            var pattern = "<>m__Finally";
            var methodRefs = this.moveNextMethod.GetMethodsInvoked();
            return methodRefs.Where(m => m.Name.StartsWith(pattern));
        }
        /// <summary>
        /// Analize the MoveNext method
        /// </summary>
        /// <param name="cfg"></param>
        /// <param name="pointsToAnalyzer"></param>
        /// <param name="protectedNodes"></param>
        /// <returns></returns>
        DependencyPTGDomain AnalyzeScopeMethod(ControlFlowGraph cfg, IteratorPointsToAnalysis pointsToAnalyzer,
                                           IEnumerable<ProtectedRowNode> protectedNodes)
        {

            // Before I did Points-to analysis beforehand the dependnecy analysis. Now I compute then together
            ////var iteratorAnalysis = new IteratorStateAnalysis(cfg, ptgs, this.equalities);
            ////var result = iteratorAnalysis.Analyze();
            //// var dependencyAnalysis = new IteratorDependencyAnalysis(this.moveNextMethod, cfg, ptgs, this.specialFields , this.equalities);

            //var nodeEntry = cfg.Entry.Successors.First();
            //var nodeExit = cfg.NormalExit;
            //nodeExit.NormalSuccessors.Add(nodeEntry);
            //nodeEntry.Predecessors.Add(nodeExit);

            var rangeAnalysis = new RangeAnalysis(cfg);
            var ranges = rangeAnalysis.Analyze();
            var exitRange = ranges[cfg.Exit.Id];
            var dependencyAnalysis = new IteratorDependencyAnalysis(this.moveNextMethod, cfg, pointsToAnalyzer, protectedNodes ,this.equalities, this.interprocManager, rangeAnalysis);
            var resultDepAnalysis = dependencyAnalysis.Analyze();

            //dependencyAnalysis.SetPreviousResult(resultDepAnalysis);

            //resultDepAnalysis = dependencyAnalysis.Analyze();

            var node = cfg.Exit;
            System.Console.Out.WriteLine("At {0}\nBefore {1}\nAfter {2}\n", node.Id, resultDepAnalysis[node.Id].Input, resultDepAnalysis[node.Id].Output);

            this.InputColumns = dependencyAnalysis.InputColumns;
            this.OutputColumns = dependencyAnalysis.OutputColumns;

            return resultDepAnalysis[node.Id].Output;
        }
        public static bool IsScopeType(IType type)
        {
            string[] scopeTypes = new[] { "RowList", "RowSet", "Row", "IEnumerable<Row>", "IEnumerator<Row>" };
            string[] scopeUsageTypes = new[] { "ScopeMapUsage",  "IEnumerable<ScopeMapUsage>", "IEnumerator<ScopeMapUsage>" };
            var basicType = type as IBasicType;
            if (basicType == null)
            {
                return false;
            }
            if (basicType.ContainingNamespace == "ScopeRuntime" && scopeTypes.Contains(basicType.Name))
            {
                return true;
            }
            if ( scopeUsageTypes.Contains(basicType.Name))
            {
                return true;
            }

            return false;
        }




        #region Methods to Compute a sort of propagation of Equalities (should be moved to extensions or utils)
        public static void PropagateExpressions(ControlFlowGraph cfg, IDictionary<IVariable, IExpression> equalities)
        {
            foreach (var node in cfg.ForwardOrder)
            {
                PropagateExpressions(node, equalities);
            }
        }

        private static void PropagateExpressions(CFGNode node, IDictionary<IVariable, IExpression> equalities)
        {
            foreach (var instruction in node.Instructions)
            {
                PropagateExpressions(instruction, equalities);
            }
        }

        private static void PropagateExpressions(IInstruction instruction, IDictionary<IVariable, IExpression> equalities)
        {
            var definition = instruction as DefinitionInstruction;

            if (definition != null && definition.HasResult)
            {
                var expr = definition.ToExpression().ReplaceVariables(equalities);
                equalities.Add(definition.Result, expr);
            }
        }

        #endregion
    }
}