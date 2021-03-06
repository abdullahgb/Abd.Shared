// ReSharper disable CheckNamespace

/*
The MIT License (MIT)

Copyright (c) 2016 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included AddOrUpdateServiceFactory
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

namespace FastExpressionCompiler
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Reflection.Emit;

    /// <summary>Compiles expression to delegate by emitting the IL directly.
    /// The emitter is ~10 times faster than Expression.Compile.</summary>
    static class ExpressionCompiler
    {
        /// <summary>First tries to compile fast and if failed (null result), then falls back to Expression.Compile.</summary>
        /// <typeparam name="T">Type of compiled delegate return result.</typeparam>
        /// <param name="lambdaExpr">Expr to compile.</param>
        /// <returns>Compiled delegate.</returns>
        public static Func<T> Compile<T>(Expression<Func<T>> lambdaExpr)
        {
            return TryCompile<Func<T>>(lambdaExpr.Body, lambdaExpr.Parameters, EmptyTypes, typeof(T))
                   ?? lambdaExpr.Compile();
        }

        /// <summary>Compiles arbitrary lambda expression to <typeparamref name="TDelegate"/>.</summary>
        /// <typeparam name="TDelegate">Should be the compatible type of delegate, otherwise case will throw.</typeparam>
        /// <param name="lambdaExpr">Lambda expression to compile.</param>
        /// <returns>compiled delegate.</returns>
        public static TDelegate Compile<TDelegate>(LambdaExpression lambdaExpr)
            where TDelegate : class
        {
            var paramExprs = lambdaExpr.Parameters;
            var paramTypes = GetParamExprTypes(paramExprs);
            var expr = lambdaExpr.Body;
            return TryCompile<TDelegate>(expr, paramExprs, paramTypes, expr.Type)
                   ?? (TDelegate)(object)lambdaExpr.Compile();
        }

        private static Type[] GetParamExprTypes(IList<ParameterExpression> paramExprs)
        {
            var paramsCount = paramExprs.Count;
            if (paramsCount == 0)
                return EmptyTypes;

            if (paramsCount == 1)
                return new[] { paramExprs[0].Type };

            var paramTypes = new Type[paramsCount];
            for (var i = 0; i < paramTypes.Length; i++)
                paramTypes[i] = paramExprs[i].Type;
            return paramTypes;
        }

        /// <summary>Compiles expression to delegate by emitting the IL.
        /// If sub-expressions are not supported by emitter, then the method returns null.
        /// The usage should be calling the method, if result is null then calling the Expression.Compile.</summary>
        /// <param name="bodyExpr">Lambda body.</param>
        /// <param name="paramExprs">Lambda parameter expressions.</param>
        /// <param name="paramTypes">The types of parameters.</param>
        /// <param name="returnType">The return type.</param>
        /// <returns>Result delegate or null, if unable to compile.</returns>
        public static TDelegate TryCompile<TDelegate>(
            Expression bodyExpr,
            IList<ParameterExpression> paramExprs,
            Type[] paramTypes,
            Type returnType) where TDelegate : class
        {
            ClosureInfo ignored = null;
            return (TDelegate)TryCompile(ref ignored,
                typeof(TDelegate), paramTypes, returnType, bodyExpr, paramExprs);
        }

        private static object TryCompile(ref ClosureInfo closureInfo,
            Type delegateType, Type[] paramTypes, Type returnType,
            Expression bodyExpr, IList<ParameterExpression> paramExprs)
        {
            if (!TryCollectBoundConstants(ref closureInfo, bodyExpr, paramExprs))
                return null;

            if (closureInfo != null)
                closureInfo.ConstructClosure();

            var method = GetDynamicMethod(paramTypes, returnType, closureInfo);

            var il = method.GetILGenerator();

            if (!EmittingVisitor.TryEmit(bodyExpr, paramExprs, il, closureInfo))
                return null;

            il.Emit(OpCodes.Ret); // emits return from generated method

            // create open delegate with closure object
            if (closureInfo == null)
                return method.CreateDelegate(delegateType);

            return method.CreateDelegate(delegateType, closureInfo.ClosureObject);
        }

        private static DynamicMethod GetDynamicMethod(Type[] paramTypes, Type returnType, ClosureInfo closureInfo)
        {
            if (closureInfo == null)
            {
                return new DynamicMethod(string.Empty, returnType, paramTypes,
                    typeof(ExpressionCompiler).GetTypeInfo().Module, skipVisibility: true);
            }

            var closureType = closureInfo.ClosureObject.GetType();
            var closureAndParamTypes = GetClosureAndParamTypes(paramTypes, closureType);

            return new DynamicMethod(string.Empty, returnType, closureAndParamTypes, closureType, skipVisibility: true);
        }

        private static Type[] GetClosureAndParamTypes(Type[] paramTypes, Type closureType)
        {
            var paramCount = paramTypes.Length;
            if (paramCount == 0)
                return new[] { closureType };

            var closureAndParamTypes = new Type[paramCount + 1];
            closureAndParamTypes[0] = closureType;
            if (paramCount == 1)
                closureAndParamTypes[1] = paramTypes[0];
            else if (paramCount > 1)
                Array.Copy(paramTypes, 0, closureAndParamTypes, 1, paramCount);

            return closureAndParamTypes;
        }

        private sealed class ClosureInfo
        {
            public List<ConstantExpression> ConstantExpressions { get; private set; }
            public List<ParameterExpression> UsedParamExpressions { get; private set; }
            public List<NestedLambdaInfo> NestedLambdas { get; private set; }

            public FieldInfo[] Fields { get; private set; }

            public bool IsArray { get; private set; }

            public object ClosureObject { get; private set; }

            public int ConstantCount { get { return ConstantExpressions == null ? 0 : ConstantExpressions.Count; } }

            public int UsedParamCount { get { return UsedParamExpressions == null ? 0 : UsedParamExpressions.Count; } }

            public int NestedLambdaCount { get { return NestedLambdas == null ? 0 : NestedLambdas.Count; } }

            public void Add(ConstantExpression expr)
            {
                if (ConstantExpressions == null)
                    ConstantExpressions = new List<ConstantExpression>();
                ConstantExpressions.Add(expr);
            }

            public void Add(ParameterExpression expr)
            {
                if (UsedParamExpressions == null)
                    UsedParamExpressions = new List<ParameterExpression>();
                UsedParamExpressions.Add(expr);
            }

            public void Add(NestedLambdaInfo nestedLambdaInfo)
            {
                if (NestedLambdas == null)
                    NestedLambdas = new List<NestedLambdaInfo>();
                NestedLambdas.Add(nestedLambdaInfo);
            }

            public void ConstructClosure()
            {
                var constantCount = ConstantCount;
                var constantPlusParamCount = constantCount + UsedParamCount;
                var totalItemCount = constantPlusParamCount + NestedLambdaCount;

                var items = new object[totalItemCount];

                var constantTypes = totalItemCount <= Closure.CreateMethods.Length ? new Type[totalItemCount] : null;

                if (ConstantExpressions != null)
                    for (var i = 0; i < ConstantExpressions.Count; i++)
                    {
                        var constantExpr = ConstantExpressions[i];
                        items[i] = constantExpr.Value;
                        if (constantTypes != null)
                            constantTypes[i] = constantExpr.Type;
                    }

                if (UsedParamExpressions != null)
                    for (var i = 0; i < UsedParamExpressions.Count; i++)
                    {
                        items[constantCount + i] = null;
                        if (constantTypes != null)
                            constantTypes[constantCount + i] = UsedParamExpressions[i].Type;
                    }

                if (NestedLambdas != null)
                    for (var i = 0; i < NestedLambdas.Count; i++)
                    {
                        var lambda = NestedLambdas[i].Lambda;
                        items[constantPlusParamCount + i] = lambda;
                        if (constantTypes != null)
                            constantTypes[constantPlusParamCount + i] = lambda.GetType();
                    }

                if (constantTypes != null)
                {
                    var createClosureMethod = Closure.CreateMethods[totalItemCount - 1];

                    var createClosure = createClosureMethod.MakeGenericMethod(constantTypes);

                    var closure = createClosure.Invoke(null, items);

                    var fields = closure.GetType().GetTypeInfo().DeclaredFields;
                    var fieldsArray = fields as FieldInfo[] ?? fields.ToArray();

                    ClosureObject = closure;
                    Fields = fieldsArray;
                }
                else
                {
                    ClosureObject = new ArrayClosure(items);
                    IsArray = true;
                }
            }
        }

        #region Closures

        internal static class Closure
        {
            public static readonly MethodInfo[] CreateMethods =
                typeof(Closure).GetTypeInfo().DeclaredMethods.ToArray();

            public static Closure<T1> CreateClosure<T1>(T1 v1)
            {
                return new Closure<T1>(v1);
            }

            public static Closure<T1, T2> CreateClosure<T1, T2>(T1 v1, T2 v2)
            {
                return new Closure<T1, T2>(v1, v2);
            }

            public static Closure<T1, T2, T3> CreateClosure<T1, T2, T3>(T1 v1, T2 v2, T3 v3)
            {
                return new Closure<T1, T2, T3>(v1, v2, v3);
            }

            public static Closure<T1, T2, T3, T4> CreateClosure<T1, T2, T3, T4>(T1 v1, T2 v2, T3 v3, T4 v4)
            {
                return new Closure<T1, T2, T3, T4>(v1, v2, v3, v4);
            }

            public static Closure<T1, T2, T3, T4, T5> CreateClosure<T1, T2, T3, T4, T5>(T1 v1, T2 v2, T3 v3, T4 v4,
                T5 v5)
            {
                return new Closure<T1, T2, T3, T4, T5>(v1, v2, v3, v4, v5);
            }

            public static Closure<T1, T2, T3, T4, T5, T6> CreateClosure<T1, T2, T3, T4, T5, T6>(T1 v1, T2 v2, T3 v3,
                T4 v4, T5 v5, T6 v6)
            {
                return new Closure<T1, T2, T3, T4, T5, T6>(v1, v2, v3, v4, v5, v6);
            }

            public static Closure<T1, T2, T3, T4, T5, T6, T7> CreateClosure<T1, T2, T3, T4, T5, T6, T7>(T1 v1, T2 v2,
                T3 v3, T4 v4, T5 v5, T6 v6, T7 v7)
            {
                return new Closure<T1, T2, T3, T4, T5, T6, T7>(v1, v2, v3, v4, v5, v6, v7);
            }

            public static Closure<T1, T2, T3, T4, T5, T6, T7, T8> CreateClosure<T1, T2, T3, T4, T5, T6, T7, T8>(
                T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8)
            {
                return new Closure<T1, T2, T3, T4, T5, T6, T7, T8>(v1, v2, v3, v4, v5, v6, v7, v8);
            }

            public static Closure<T1, T2, T3, T4, T5, T6, T7, T8, T9> CreateClosure<T1, T2, T3, T4, T5, T6, T7, T8, T9>(
                T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8, T9 v9)
            {
                return new Closure<T1, T2, T3, T4, T5, T6, T7, T8, T9>(v1, v2, v3, v4, v5, v6, v7, v8, v9);
            }

            public static Closure<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> CreateClosure<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(
                T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8, T9 v9, T10 v10)
            {
                return new Closure<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(v1, v2, v3, v4, v5, v6, v7, v8, v9, v10);
            }
        }

        internal sealed class Closure<T1>
        {
            public T1 V1;

            public Closure(T1 v1)
            {
                V1 = v1;
            }
        }

        internal sealed class Closure<T1, T2>
        {
            public T1 V1;
            public T2 V2;

            public Closure(T1 v1, T2 v2)
            {
                V1 = v1;
                V2 = v2;
            }
        }

        internal sealed class Closure<T1, T2, T3>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;

            public Closure(T1 v1, T2 v2, T3 v3)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4, T5>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;
            public T5 V5;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
                V5 = v5;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4, T5, T6>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;
            public T5 V5;
            public T6 V6;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
                V5 = v5;
                V6 = v6;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4, T5, T6, T7>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;
            public T5 V5;
            public T6 V6;
            public T7 V7;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
                V5 = v5;
                V6 = v6;
                V7 = v7;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4, T5, T6, T7, T8>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;
            public T5 V5;
            public T6 V6;
            public T7 V7;
            public T8 V8;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
                V5 = v5;
                V6 = v6;
                V7 = v7;
                V8 = v8;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4, T5, T6, T7, T8, T9>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;
            public T5 V5;
            public T6 V6;
            public T7 V7;
            public T8 V8;
            public T9 V9;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8, T9 v9)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
                V5 = v5;
                V6 = v6;
                V7 = v7;
                V8 = v8;
                V9 = v9;
            }
        }

        internal sealed class Closure<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
        {
            public T1 V1;
            public T2 V2;
            public T3 V3;
            public T4 V4;
            public T5 V5;
            public T6 V6;
            public T7 V7;
            public T8 V8;
            public T9 V9;
            public T10 V10;

            public Closure(T1 v1, T2 v2, T3 v3, T4 v4, T5 v5, T6 v6, T7 v7, T8 v8, T9 v9, T10 v10)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
                V4 = v4;
                V5 = v5;
                V6 = v6;
                V7 = v7;
                V8 = v8;
                V9 = v9;
                V10 = v10;
            }
        }

        internal sealed class ArrayClosure
        {
            public readonly object[] Constants;
            public static FieldInfo ArrayField = typeof(ArrayClosure).GetTypeInfo().DeclaredFields.First(f => !f.IsStatic);

            public ArrayClosure(object[] constants)
            {
                Constants = constants;
            }
        }

        #endregion

        #region Collect Bound Constants

        private sealed class NestedLambdaInfo
        {
            public readonly object Lambda;
            public readonly Expression Expr;
            public readonly ClosureInfo ClosureInfo;

            public NestedLambdaInfo(object lambda, Expression expr, ClosureInfo closureInfo)
            {
                Lambda = lambda;
                Expr = expr;
                ClosureInfo = closureInfo;
            }
        }

        private static bool IsBoundConstant(object value)
        {
            return value != null &&
                   !(value is int || value is double || value is bool ||
                     value is string || value is Type || value.GetType().GetTypeInfo().IsEnum);
        }

        private static readonly Type[] EmptyTypes = new Type[0];

        // @paramExprs is required for nested lambda compilation
        private static bool TryCollectBoundConstants(ref ClosureInfo closure, Expression expr, IList<ParameterExpression> paramExprs)
        {
            if (expr == null)
                return false;

            switch (expr.NodeType)
            {
                case ExpressionType.Constant:
                    var constantExpr = (ConstantExpression)expr;
                    var constantValue = constantExpr.Value;

                    if (constantValue is Delegate ||
                        IsBoundConstant(constantValue))
                    {
                        closure = closure ?? new ClosureInfo();
                        closure.Add(constantExpr);
                    }

                    break;

                case ExpressionType.Parameter:
                    // if parameter is used but no passed we assume that it should be in closure and set by outer lambda
                    var paramExpr = (ParameterExpression)expr;
                    if (paramExprs.IndexOf(paramExpr) == -1)
                    {
                        closure = closure ?? new ClosureInfo();
                        closure.Add(paramExpr);
                    }
                    break;

                case ExpressionType.Call:
                    var methodCallExpr = (MethodCallExpression)expr;
                    var methodOwnerExpr = methodCallExpr.Object;

                    return (methodOwnerExpr == null
                            || TryCollectBoundConstants(ref closure, methodOwnerExpr, paramExprs))
                           && TryCollectBoundConstants(ref closure, methodCallExpr.Arguments, paramExprs);

                case ExpressionType.MemberAccess:
                    return TryCollectBoundConstants(ref closure, ((MemberExpression)expr).Expression, paramExprs);

                case ExpressionType.New:
                    return TryCollectBoundConstants(ref closure, ((NewExpression)expr).Arguments, paramExprs);

                case ExpressionType.NewArrayInit:
                    return TryCollectBoundConstants(ref closure, ((NewArrayExpression)expr).Expressions,
                        paramExprs);

                // property initializer
                case ExpressionType.MemberInit:
                    var memberInitExpr = (MemberInitExpression)expr;
                    if (!TryCollectBoundConstants(ref closure, memberInitExpr.NewExpression, paramExprs))
                        return false;

                    var memberBindings = memberInitExpr.Bindings;
                    for (var i = 0; i < memberBindings.Count; ++i)
                    {
                        var memberBinding = memberBindings[i];
                        if (memberBinding.BindingType == MemberBindingType.Assignment &&
                            !TryCollectBoundConstants(ref closure, ((MemberAssignment)memberBinding).Expression,
                                paramExprs))
                            return false;
                    }
                    break;

                // nested lambda
                case ExpressionType.Lambda:

                    var lambdaExpr = (LambdaExpression)expr;
                    var lambdaParamExprs = lambdaExpr.Parameters;
                    var paramTypes = GetParamExprTypes(lambdaParamExprs);

                    ClosureInfo nestedClosure = null;
                    var nestedLambda = TryCompile(ref nestedClosure,
                        lambdaExpr.Type, paramTypes, lambdaExpr.Body.Type, lambdaExpr.Body, lambdaParamExprs);

                    if (nestedLambda == null)
                        return false;

                    var nestedLambdaInfo = new NestedLambdaInfo(nestedLambda, expr, nestedClosure);

                    closure = closure ?? new ClosureInfo();
                    closure.Add(nestedLambdaInfo);

                    // if nested parameter is no matched with any outer parameter, that ensure it goes to outer closure
                    if (nestedClosure != null && nestedClosure.UsedParamExpressions != null)
                    {
                        var nestedClosedParams = nestedClosure.UsedParamExpressions;
                        for (var i = 0; i < nestedClosedParams.Count; i++)
                        {
                            var nestedClosedParamExpr = nestedClosedParams[i];
                            if (paramExprs.Count == 0 ||
                                paramExprs.IndexOf(nestedClosedParamExpr) == -1)
                            {
                                closure.Add(nestedClosedParamExpr);
                            }
                        }
                    }

                    break;

                case ExpressionType.Invoke:
                    var invocationExpr = (InvocationExpression)expr;
                    return TryCollectBoundConstants(ref closure, invocationExpr.Expression, paramExprs)
                           && TryCollectBoundConstants(ref closure, invocationExpr.Arguments, paramExprs);

                default:
                    var unaryExpr = expr as UnaryExpression;
                    if (unaryExpr != null)
                        return TryCollectBoundConstants(ref closure, unaryExpr.Operand, paramExprs);

                    var binaryExpr = expr as BinaryExpression;
                    if (binaryExpr != null)
                        return TryCollectBoundConstants(ref closure, binaryExpr.Left, paramExprs)
                               && TryCollectBoundConstants(ref closure, binaryExpr.Right, paramExprs);

                    break;
            }

            return true;
        }

        private static bool TryCollectBoundConstants(ref ClosureInfo closure, IList<Expression> exprs, IList<ParameterExpression> paramExprs)
        {
            var count = exprs.Count;
            for (var i = 0; i < count; i++)
                if (!TryCollectBoundConstants(ref closure, exprs[i], paramExprs))
                    return false;
            return true;
        }

        #endregion

        /// <summary>Supports emitting of selected expressions, e.g. lambdaExpr are not supported yet.
        /// When emitter find not supported expression it will return false from <see cref="TryEmit"/>, so I could fallback
        /// to normal and slow Expression.Compile.</summary>
        private static class EmittingVisitor
        {
            private static readonly MethodInfo _getDelegateTargetProperty =
                typeof(Delegate).GetTypeInfo().DeclaredProperties.First(p => p.Name == "Target").GetMethod;

            public static bool TryEmit(Expression expr, IList<ParameterExpression> paramExprs, ILGenerator il, ClosureInfo closure)
            {
                switch (expr.NodeType)
                {
                    case ExpressionType.Parameter:
                        return EmitParameter((ParameterExpression)expr, paramExprs, il, closure);
                    case ExpressionType.Convert:
                        return EmitConvert((UnaryExpression)expr, paramExprs, il, closure);
                    case ExpressionType.ArrayIndex:
                        return EmitArrayIndex((BinaryExpression)expr, paramExprs, il, closure);
                    case ExpressionType.Constant:
                        return EmitConstant((ConstantExpression)expr, il, closure);
                    case ExpressionType.New:
                        return EmitNew((NewExpression)expr, paramExprs, il, closure);
                    case ExpressionType.NewArrayInit:
                        return EmitNewArray((NewArrayExpression)expr, paramExprs, il, closure);
                    case ExpressionType.MemberInit:
                        return EmitMemberInit((MemberInitExpression)expr, paramExprs, il, closure);
                    case ExpressionType.Call:
                        return EmitMethodCall((MethodCallExpression)expr, paramExprs, il, closure);
                    case ExpressionType.MemberAccess:
                        return EmitMemberAccess((MemberExpression)expr, paramExprs, il, closure);

                    case ExpressionType.Lambda:
                        return EmitNestedLambda((LambdaExpression)expr, paramExprs, il, closure);

                    case ExpressionType.Invoke:
                        return EmitInvokeLambda((InvocationExpression)expr, paramExprs, il, closure);

                    case ExpressionType.GreaterThan:
                    case ExpressionType.GreaterThanOrEqual:
                    case ExpressionType.LessThan:
                    case ExpressionType.LessThanOrEqual:
                    case ExpressionType.Equal:
                    case ExpressionType.NotEqual:
                        return EmitComparison((BinaryExpression)expr, paramExprs, il, closure);
                    default:
                        return false;
                }
            }

            private static bool EmitParameter(ParameterExpression p, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var paramIndex = ps.IndexOf(p);
                if (paramIndex == -1)
                {
                    // means that parameter isn't passed, and probably part of outer scope,
                    // so it should be loaded from closure
                    if (closure == null)
                        return false;

                    var usedParamIndex = closure.UsedParamExpressions.IndexOf(p);
                    if (usedParamIndex == -1)
                        return false;  // what??? no chance

                    var closureItemIndex = usedParamIndex + closure.ConstantCount;

                    LoadClosureFieldOrItem(il, closure, closureItemIndex, p.Type);
                    return true;
                }

                if (closure != null)
                    paramIndex += 1; // shift parameter indeces by one, because the first one will be closure

                LoadParamArg(il, paramIndex);

                return true;
            }

            private static void LoadParamArg(ILGenerator il, int paramIndex)
            {
                switch (paramIndex)
                {
                    case 0:
                        il.Emit(OpCodes.Ldarg_0);
                        break;
                    case 1:
                        il.Emit(OpCodes.Ldarg_1);
                        break;
                    case 2:
                        il.Emit(OpCodes.Ldarg_2);
                        break;
                    case 3:
                        il.Emit(OpCodes.Ldarg_3);
                        break;
                    default:
                        if (paramIndex <= byte.MaxValue)
                            il.Emit(OpCodes.Ldarg_S, (byte)paramIndex);
                        else
                            il.Emit(OpCodes.Ldarg, paramIndex);
                        break;
                }
            }

            private static bool EmitBinary(BinaryExpression b, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = TryEmit(b.Left, ps, il, closure);
                if (ok)
                    ok = TryEmit(b.Right, ps, il, closure);
                // skips TryEmit(b.Conversion) for NodeType.Coalesce (?? operation)
                return ok;
            }

            private static bool EmitMany(IList<Expression> es, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                for (int i = 0, n = es.Count; i < n; i++)
                    if (!TryEmit(es[i], ps, il, closure))
                        return false;
                return true;
            }

            private static bool EmitConvert(UnaryExpression node, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = TryEmit(node.Operand, ps, il, closure);
                if (ok)
                {
                    var convertTargetType = node.Type;
                    if (convertTargetType == typeof(object))
                        return false;
                    il.Emit(OpCodes.Castclass, convertTargetType);
                }
                return ok;
            }

            private static bool EmitConstant(ConstantExpression constantExpr, ILGenerator il, ClosureInfo closure)
            {
                var constant = constantExpr.Value;
                var constantType = constantExpr.Type;

                if (constant == null)
                {
                    il.Emit(OpCodes.Ldnull);
                }
                else if (constant is int || constant.GetType().GetTypeInfo().IsEnum)
                {
                    EmitLoadConstantInt(il, (int)constant);
                }
                else if (constant is double)
                {
                    il.Emit(OpCodes.Ldc_R8, (double)constant);
                }
                else if (constant is bool)
                {
                    il.Emit((bool)constant ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                }
                else if (constant is string)
                {
                    il.Emit(OpCodes.Ldstr, (string)constant);
                }
                else if (constant is Type)
                {
                    il.Emit(OpCodes.Ldtoken, (Type)constant);
                    var getTypeFromHandle = typeof(Type).GetTypeInfo().DeclaredMethods.First(m => m.Name == "GetTypeFromHandle");
                    il.Emit(OpCodes.Call, getTypeFromHandle);
                }
                else if (closure != null)
                {
                    var constantIndex = closure.ConstantExpressions.IndexOf(constantExpr);
                    if (constantIndex == -1)
                        return false;

                    LoadClosureFieldOrItem(il, closure, constantIndex, constantType);
                }
                else
                    return false;

                // boxing the value type, otherwise we can get a strange result when 0 is treated as Null.
                if (constantType == typeof(object) &&
                    constant != null && constant.GetType().GetTypeInfo().IsValueType)
                    il.Emit(OpCodes.Box, constant.GetType());

                return true;
            }

            private static void LoadClosureFieldOrItem(ILGenerator il, ClosureInfo closure, int constantIndex, Type constantType)
            {
                // load closure argument: Closure object or Closure array
                il.Emit(OpCodes.Ldarg_0);

                if (!closure.IsArray)
                {
                    // load closure field
                    il.Emit(OpCodes.Ldfld, closure.Fields[constantIndex]);
                }
                else
                {
                    // load array field
                    il.Emit(OpCodes.Ldfld, ArrayClosure.ArrayField);

                    // load array item index
                    EmitLoadConstantInt(il, constantIndex);

                    // load item from index
                    il.Emit(OpCodes.Ldelem_Ref);

                    // cast if needed
                    if (constantType != typeof(object))
                        il.Emit(OpCodes.Castclass, constantType);
                }
            }

            private static bool EmitNew(NewExpression n, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = EmitMany(n.Arguments, ps, il, closure);
                if (ok)
                    il.Emit(OpCodes.Newobj, n.Constructor);
                return ok;
            }

            private static bool EmitNewArray(NewArrayExpression na, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var elems = na.Expressions;
                var arrType = na.Type;
                var elemType = arrType.GetElementType();
                var isElemOfValueType = elemType.GetTypeInfo().IsValueType;

                var arrVar = il.DeclareLocal(arrType);

                EmitLoadConstantInt(il, elems.Count);
                il.Emit(OpCodes.Newarr, elemType);
                il.Emit(OpCodes.Stloc, arrVar);

                var ok = true;
                for (int i = 0, n = elems.Count; i < n && ok; i++)
                {
                    il.Emit(OpCodes.Ldloc, arrVar);
                    EmitLoadConstantInt(il, i);

                    // loading element address for later copying of value into it.
                    if (isElemOfValueType)
                        il.Emit(OpCodes.Ldelema, elemType);

                    ok = TryEmit(elems[i], ps, il, closure);
                    if (ok)
                    {
                        if (isElemOfValueType)
                            il.Emit(OpCodes.Stobj, elemType); // store element of value type by array element address
                        else
                            il.Emit(OpCodes.Stelem_Ref);
                    }
                }

                il.Emit(OpCodes.Ldloc, arrVar);
                return ok;
            }

            private static bool EmitArrayIndex(BinaryExpression ai, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = EmitBinary(ai, ps, il, closure);
                if (ok)
                    il.Emit(OpCodes.Ldelem_Ref);
                return ok;
            }

            private static bool EmitMemberInit(MemberInitExpression mi, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = EmitNew(mi.NewExpression, ps, il, closure);
                if (!ok) return false;

                var obj = il.DeclareLocal(mi.Type);
                il.Emit(OpCodes.Stloc, obj);

                var bindings = mi.Bindings;
                for (int i = 0, n = bindings.Count; i < n; i++)
                {
                    var binding = bindings[i];
                    if (binding.BindingType != MemberBindingType.Assignment)
                        return false;
                    il.Emit(OpCodes.Ldloc, obj);

                    ok = TryEmit(((MemberAssignment)binding).Expression, ps, il, closure);
                    if (!ok) return false;

                    if (binding.Member is PropertyInfo prop)
                    {
                        var setMethod = prop.SetMethod;
                        if (setMethod == null)
                            return false;
                        EmitMethodCall(setMethod, il);
                    }
                    else
                    {
                        if (!(binding.Member is FieldInfo field))
                            return false;
                        il.Emit(OpCodes.Stfld, field);
                    }
                }

                il.Emit(OpCodes.Ldloc, obj);
                return true;
            }

            private static bool EmitMethodCall(MethodCallExpression m, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = true;
                if (m.Object != null)
                    ok = TryEmit(m.Object, ps, il, closure);

                if (ok && m.Arguments.Count != 0)
                    ok = EmitMany(m.Arguments, ps, il, closure);

                if (ok)
                    EmitMethodCall(m.Method, il);

                return ok;
            }

            private static bool EmitMemberAccess(MemberExpression m, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                if (m.Expression != null)
                {
                    var ok = TryEmit(m.Expression, ps, il, closure);
                    if (!ok) return false;
                }

                if (m.Member is FieldInfo field)
                {
                    il.Emit(field.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, field);
                    return true;
                }

                if (m.Member is PropertyInfo property)
                {
                    var getMethod = property.GetMethod;
                    if (getMethod == null)
                        return false;
                    EmitMethodCall(getMethod, il);
                }

                return true;
            }

            private static bool EmitNestedLambda(LambdaExpression lambdaExpr, IList<ParameterExpression> paramExprs, ILGenerator il, ClosureInfo closure)
            {
                var nestedLambdas = closure.NestedLambdas;
                var lambdaIndex = nestedLambdas.Count - 1;
                while (lambdaIndex >= 0 && nestedLambdas[lambdaIndex].Expr != lambdaExpr)
                    --lambdaIndex;

                if (lambdaIndex == -1)
                    return false;

                var lambdaInfo = nestedLambdas[lambdaIndex];
                var lambda = lambdaInfo.Lambda;
                var lambdaType = lambda.GetType();

                var closureItemIndex = lambdaIndex + closure.ConstantCount + closure.UsedParamCount;

                LoadClosureFieldOrItem(il, closure, closureItemIndex, lambdaType);

                var lambdaClosure = lambdaInfo.ClosureInfo;
                if (lambdaClosure == null ||
                    lambdaClosure.UsedParamExpressions == null)
                    return true;

                // sets closure param placeholder fields to the param values
                var closedParamExprs = lambdaClosure.UsedParamExpressions;
                for (var i = 0; i < closedParamExprs.Count; i++)
                {
                    var closedParamExpr = closedParamExprs[i];

                    // copy lambda fied on stack in ordet to set it Target.Param to param value
                    il.Emit(OpCodes.Dup);

                    // load lambda.Target property
                    EmitMethodCall(_getDelegateTargetProperty, il);

                    // params go after constants
                    var closedParamIndex = i + lambdaClosure.ConstantCount;

                    if (lambdaClosure.IsArray)
                    {
                        // load array field
                        il.Emit(OpCodes.Ldfld, ArrayClosure.ArrayField);

                        // load array item index
                        EmitLoadConstantInt(il, closedParamIndex);
                    }

                    var paramIndex = paramExprs.IndexOf(closedParamExpr);
                    if (paramIndex != -1) // load param from inout params
                    {
                        // +1 is set cause of added first closure argument
                        LoadParamArg(il, paramIndex + 1);
                    }
                    else // load parameter from outer closure
                    {
                        if (closure.UsedParamExpressions == null)
                            return false; // impossible, may be throw

                        var outerClosureParamIndex = closure.UsedParamExpressions.IndexOf(closedParamExpr);
                        if (outerClosureParamIndex == -1)
                            return false; // impossible, may be throw

                        var outerClosureParamItemIndex = closure.ConstantCount + outerClosureParamIndex;
                        LoadClosureFieldOrItem(il, closure, outerClosureParamItemIndex, closedParamExpr.Type);
                    }

                    if (lambdaClosure.IsArray)
                    {
                        // box value types before setting the object array item
                        if (closedParamExpr.Type.GetTypeInfo().IsValueType)
                            il.Emit(OpCodes.Box, closedParamExpr.Type);

                        // load item from index
                        il.Emit(OpCodes.Stelem_Ref);
                    }
                    else
                    {
                        var closedParamField = lambdaClosure.Fields[closedParamIndex];
                        il.Emit(OpCodes.Stfld, closedParamField);
                    }
                }

                return true;
            }

            private static bool EmitInvokeLambda(InvocationExpression expr, IList<ParameterExpression> paramExprs, ILGenerator il, ClosureInfo closure)
            {
                if (TryEmit(expr.Expression, paramExprs, il, closure) &&
                    EmitMany(expr.Arguments, paramExprs, il, closure))
                {
                    var invokeMethod = expr.Expression.Type.GetTypeInfo().DeclaredMethods.First(m => m.Name == "Invoke");
                    EmitMethodCall(invokeMethod, il);
                    return true;
                }

                return false;
            }

            private static bool EmitComparison(BinaryExpression c, IList<ParameterExpression> ps, ILGenerator il, ClosureInfo closure)
            {
                var ok = EmitBinary(c, ps, il, closure);
                if (ok)
                {
                    switch (c.NodeType)
                    {
                        case ExpressionType.Equal:
                            il.Emit(OpCodes.Ceq);
                            break;
                        case ExpressionType.LessThan:
                            il.Emit(OpCodes.Clt);
                            break;
                        case ExpressionType.GreaterThan:
                            il.Emit(OpCodes.Cgt);
                            break;
                        case ExpressionType.NotEqual:
                            il.Emit(OpCodes.Ceq);
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ceq);
                            break;
                        case ExpressionType.LessThanOrEqual:
                            il.Emit(OpCodes.Cgt);
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ceq);
                            break;
                        case ExpressionType.GreaterThanOrEqual:
                            il.Emit(OpCodes.Clt);
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ceq);
                            break;
                    }
                }
                return ok;
            }

            private static void EmitMethodCall(MethodInfo method, ILGenerator il)
            {
                il.Emit(method.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, method);
            }

            private static void EmitLoadConstantInt(ILGenerator il, int i)
            {
                switch (i)
                {
                    case 0:
                        il.Emit(OpCodes.Ldc_I4_0);
                        break;
                    case 1:
                        il.Emit(OpCodes.Ldc_I4_1);
                        break;
                    case 2:
                        il.Emit(OpCodes.Ldc_I4_2);
                        break;
                    case 3:
                        il.Emit(OpCodes.Ldc_I4_3);
                        break;
                    case 4:
                        il.Emit(OpCodes.Ldc_I4_4);
                        break;
                    case 5:
                        il.Emit(OpCodes.Ldc_I4_5);
                        break;
                    case 6:
                        il.Emit(OpCodes.Ldc_I4_6);
                        break;
                    case 7:
                        il.Emit(OpCodes.Ldc_I4_7);
                        break;
                    case 8:
                        il.Emit(OpCodes.Ldc_I4_8);
                        break;
                    default:
                        il.Emit(OpCodes.Ldc_I4, i);
                        break;
                }
            }
        }
    }
}