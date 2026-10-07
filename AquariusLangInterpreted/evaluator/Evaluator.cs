using System;
using System.Collections.Generic;
using AquariusLang.ast;
using AquariusLang.Object;
using AquariusLang.utils;
using Environment = AquariusLang.Object.Environment;

namespace AquariusLang.evaluator {

    /// <summary>
    /// These are for not creating a new object each time returning primitives with repeated values.
    /// For example, a boolean with true or false value might appear lots of times. It's better if
    /// just create the object once and re-use it in every encounter.
    /// </summary>
    public struct RepeatedPrimitives {
        public static readonly NullObj NULL = new NullObj();
        public static readonly BooleanObj TRUE = new BooleanObj(true);
        public static readonly BooleanObj FALSE = new BooleanObj(false);
        public static readonly BreakObj BREAK = new BreakObj();
    }

    public class Evaluator {
        private Builtins builtins;

        private Evaluator() {

        }

        private Evaluator(Builtins builtins) {
            this.builtins = builtins;
        }

        public static Evaluator NewInstance(Builtins builtins) {
            return new Evaluator(builtins);
        }

        public IObject Eval(INode node, Environment environment) {
            Type nodeType = node.GetType();

            switch (nodeTypeMap[nodeType]) {
                case NodeTypeMapValue.ASTMapValue:
                    return evalTree((AbstractSyntaxTree)node, environment);

                case NodeTypeMapValue.BlockMapValue:
                    return evalBlockStatement((BlockStatement)node, environment);

                case NodeTypeMapValue.ExpressMapValue:
                    return Eval(((ExpressionStatement)node).Expression, environment);

                case NodeTypeMapValue.ReturnMapValue:
                    IObject rtnObj = Eval(((ReturnStatement)node).ReturnValue, environment);
                    if (isError(rtnObj)) {
                        return rtnObj;
                    }

                    return new ReturnValueObj(rtnObj);

                case NodeTypeMapValue.BreakMapValue:
                    return RepeatedPrimitives.BREAK;

                case NodeTypeMapValue.LetMapValue:
                    LetStatement letStatement = (LetStatement)node;
                    IObject letObj = Eval(letStatement.Value, environment);
                    if (isError(letObj)) {
                        return letObj;
                    }

                    environment.Create(letStatement.Name.Value, letObj); // Saving value to newly created variable.
                    break;

                case NodeTypeMapValue.IntMapValue:
                    return new IntegerObj(((IntegerLiteral)node).Value);

                case NodeTypeMapValue.FloatMapValue:
                    return new FloatObj(((FloatLiteral)node).Value);

                case NodeTypeMapValue.DoubleMapValue:
                    return new DoubleObj(((DoubleLiteral)node).Value);

                case NodeTypeMapValue.BoolMapValue:
                    return nativeBoolToBoolObj(((BooleanLiteral)node).Value);

                case NodeTypeMapValue.IncrementMapValue:
                    return evalIncrementExpression((IncrementExpression)node, environment);

                case NodeTypeMapValue.PrefixMapValue:
                    IObject right = Eval(((PrefixExpression)node).Right, environment);
                    if (isError(right)) {
                        return right;
                    }

                    return evalPrefixExpression(((PrefixExpression)node).Operator, right);

                case NodeTypeMapValue.InfixMapValue:
                    InfixExpression _node = (InfixExpression)node;

                    IObject _left = Eval(_node.Left, environment);
                    if (isError(_left)) {
                        return _left;
                    }

                    switch (_node.Operator) {
                        case "=":
                            IObject _right = Eval(_node.Right, environment);
                            if (isError(_right)) {
                                return _right;
                            }

                            if (_node.Left is Identifier leftIdent) {
                                environment.Set(leftIdent.Value, _right);
                            } else if (_node.Left is ArrayObj arrayObj) {
                                throw new NotImplementedException("Value-reassignment for ArrayObj not implemented yet.");
                            } else if (_node.Left is HashObj hashObj) {
                                throw new NotImplementedException("Value-reassignment for HashObj not implemented yet.");
                            } else {
                                return NewError($"Left operand for = operator not identifier. Got={_node.Left.GetType()}");
                            }

                            break;
                        case "+=":
                            IObject __right = Eval(_node.Right, environment);
                            if (isError(__right)) {
                                return __right;
                            }

                            if (_node.Left is Identifier _leftIdent) {
                                IObject identVal = evalIdentifier(_leftIdent, environment);
                                string identType = identVal.Type();
                                string rightType = __right.Type();
                                if (ObjectType.IsNumber(rightType)) {
                                    if (ObjectType.IsNumber(identType)) {
                                        /*
                                         * Cast the number type to the identifier's number type, no matter whether the right operand's type is int, float, or double.
                                         */
                                        switch (identType) {
                                            case ObjectType.INTEGER_OBJ:
                                                environment.Set(_leftIdent.Value,
                                                    new IntegerObj((int)(((IntegerObj)identVal).Value +
                                                                         ((INumberObj)__right).GetNumValue())));
                                                break;
                                            case ObjectType.FLOAT_OBJ:
                                                environment.Set(_leftIdent.Value,
                                                    new FloatObj((float)(((FloatObj)identVal).Value +
                                                                         ((INumberObj)__right).GetNumValue())));
                                                break;
                                            case ObjectType.DOUBLE_OBJ:
                                                environment.Set(_leftIdent.Value,
                                                    new DoubleObj(((DoubleObj)identVal).Value +
                                                                  ((INumberObj)__right).GetNumValue()));
                                                break;
                                        }
                                    } else {
                                        return NewError($"Incorrect left operand type for {identType} += NUMBER");
                                    }
                                } else if (rightType == ObjectType.STRING_OBJ) {
                                    if (identType == ObjectType.STRING_OBJ) {
                                        environment.Set(_leftIdent.Value,
                                            new StringObj(((StringObj)identVal).Value + ((StringObj)__right).Value));
                                    } else {
                                        return NewError($"Incorrect left operand type for STRING +=: {identVal.Type()}");
                                    }
                                } else {
                                    return NewError($"{rightType} as right operand type for += doesn't exist.");
                                }
                            } else {
                                return NewError($"Left operand for += operator not identifier. Got={_node.Left.GetType()}");
                            }

                            break;
                        case "-=":
                            IObject ___right = Eval(_node.Right, environment);
                            if (isError(___right)) {
                                return ___right;
                            }

                            if (_node.Left is Identifier __leftIdent) {
                                IObject identVal = evalIdentifier(__leftIdent, environment);
                                string identType = identVal.Type();
                                string rightType = ___right.Type();
                                if (ObjectType.IsNumber(rightType)) {
                                    if (ObjectType.IsNumber(identType)) {
                                        switch (identType) {
                                            case ObjectType.INTEGER_OBJ:
                                                environment.Set(__leftIdent.Value,
                                                    new IntegerObj((int)(((IntegerObj)identVal).Value -
                                                                         ((INumberObj)___right).GetNumValue())));
                                                break;
                                            case ObjectType.FLOAT_OBJ:
                                                environment.Set(__leftIdent.Value,
                                                    new FloatObj((float)(((FloatObj)identVal).Value -
                                                                         ((INumberObj)___right).GetNumValue())));
                                                break;
                                            case ObjectType.DOUBLE_OBJ:
                                                environment.Set(__leftIdent.Value,
                                                    new DoubleObj(((DoubleObj)identVal).Value -
                                                                  ((INumberObj)___right).GetNumValue()));
                                                break;
                                        }
                                    } else {
                                        return NewError($"Incorrect left operand type for {identType} -= NUMBER");
                                    }
                                } else {
                                    return NewError($"{___right.Type()} as right operand type for -= doesn't exist.");
                                }
                            } else {
                                return NewError($"Left operand for -= operator not identifier. Got={_node.Left.GetType()}");
                            }

                            break;
                        case "*=":
                            IObject ____right = Eval(_node.Right, environment);
                            if (isError(____right)) {
                                return ____right;
                            }

                            if (_node.Left is Identifier ___leftIdent) {
                                IObject identVal = evalIdentifier(___leftIdent, environment);
                                string identType = identVal.Type();
                                string rightType = ____right.Type();
                                if (ObjectType.IsNumber(rightType)) {
                                    if (ObjectType.IsNumber(identType)) {
                                        switch (identType) {
                                            case ObjectType.INTEGER_OBJ:
                                                environment.Set(___leftIdent.Value,
                                                    new IntegerObj((int)(((IntegerObj)identVal).Value *
                                                                         ((INumberObj)____right).GetNumValue())));
                                                break;
                                            case ObjectType.FLOAT_OBJ:
                                                environment.Set(___leftIdent.Value,
                                                    new FloatObj((float)(((FloatObj)identVal).Value *
                                                                         ((INumberObj)____right).GetNumValue())));
                                                break;
                                            case ObjectType.DOUBLE_OBJ:
                                                environment.Set(___leftIdent.Value,
                                                    new DoubleObj(((DoubleObj)identVal).Value *
                                                                  ((INumberObj)____right).GetNumValue()));
                                                break;
                                        }
                                    } else {
                                        return NewError($"Incorrect left operand type for {identType} *= NUMBER");
                                    }
                                } else {
                                    return NewError($"{____right.Type()} as right operand type for *= doesn't exist.");
                                }
                            } else {
                                return NewError($"Left operand for *= operator not identifier. Got={_node.Left.GetType()}");
                            }

                            break;
                        case "/=":
                            IObject _____right = Eval(_node.Right, environment);
                            if (isError(_____right)) {
                                return _____right;
                            }

                            if (_node.Left is Identifier ____leftIdent) {
                                IObject identVal = evalIdentifier(____leftIdent, environment);
                                string identType = identVal.Type();
                                string rightType = _____right.Type();
                                if (ObjectType.IsNumber(rightType)) {
                                    if (ObjectType.IsNumber(identType)) {
                                        switch (identType) {
                                            case ObjectType.INTEGER_OBJ:
                                                environment.Set(____leftIdent.Value,
                                                    new IntegerObj((int)(((IntegerObj)identVal).Value /
                                                                         ((INumberObj)_____right).GetNumValue())));
                                                break;
                                            case ObjectType.FLOAT_OBJ:
                                                environment.Set(____leftIdent.Value,
                                                    new FloatObj((float)(((FloatObj)identVal).Value /
                                                                         ((INumberObj)_____right).GetNumValue())));
                                                break;
                                            case ObjectType.DOUBLE_OBJ:
                                                environment.Set(____leftIdent.Value,
                                                    new DoubleObj(((DoubleObj)identVal).Value /
                                                                  ((INumberObj)_____right).GetNumValue()));
                                                break;
                                        }
                                    } else {
                                        return NewError($"Incorrect left operand type for {identType} /= NUMBER");
                                    }
                                } else {
                                    return NewError($"{_____right.Type()} as right operand type for *= doesn't exist.");
                                }
                            } else {
                                return NewError($"Left operand for *= operator not identifier. Got={_node.Left.GetType()}");
                            }

                            break;
                        case ".":
                            if (_left is ModuleObj moduleObj) {
                                if (_node.Right is Identifier member) {
                                    IObject memberValue = moduleObj._Environment.Get(member.Value, out bool found);
                                    return found ? memberValue : NewError($"Module member not found: {member.Value}");
                                }
                                if (_node.Right is CallExpression memberCall) {
                                    // Resolve the function in the module, but arguments in the caller's scope.
                                    IObject memberFunction = Eval(memberCall.Function, moduleObj._Environment);
                                    if (isError(memberFunction)) return memberFunction;
                                    IObject[] memberArguments = evalExpressions(memberCall.Arguments, environment);
                                    if (memberArguments.Length == 1 && isError(memberArguments[0])) return memberArguments[0];
                                    return applyFunction(memberFunction, memberArguments, environment);
                                }
                            }
                            return NewError($"Cannot access member of {_left.Type()}");
                        default:
                            IObject _______right = Eval(_node.Right, environment);
                            if (isError(_______right)) {
                                return _______right;
                            }

                            return evalInfixExpression(_node.Operator, _left, _______right);
                    }

                    break;

                case NodeTypeMapValue.IfMapValue:
                    return evalIfExpression((IfExpression)node, environment);

                case NodeTypeMapValue.IdentMapValue:
                    return evalIdentifier((Identifier)node, environment);

                case NodeTypeMapValue.FuncMapValue:
                    FunctionLiteral functionLiteralNode = (FunctionLiteral)node;
                    Identifier[] parameters = functionLiteralNode.Parameters;
                    BlockStatement body = functionLiteralNode.Body;
                    return new FunctionObj(parameters, body, environment);

                case NodeTypeMapValue.CallMapValue:
                    CallExpression callExpressionNode = (CallExpression)node;
                    IObject function = Eval(callExpressionNode.Function, environment); // function should be of type FunctionObj.
                    if (isError(function)) {
                        return function;
                    }

                    IObject[] args = evalExpressions(callExpressionNode.Arguments, environment);
                    if (args.Length == 1 && isError(args[0])) {
                        return args[0];
                    }

                    return applyFunction(function, args, environment);

                case NodeTypeMapValue.StringMapValue:
                    return new StringObj(((StringLiteral)node).Value);

                case NodeTypeMapValue.ArrayMapValue:
                    IObject[] elements = evalExpressions(((ArrayLiteral)node).Elements, environment);
                    if (elements.Length == 1 && isError(elements[0])) {
                        return elements[0];
                    }

                    return new ArrayObj(elements);

                case NodeTypeMapValue.IndexMapValue:
                    IndexExpression indexNode = (IndexExpression)node;
                    IObject left = Eval(indexNode.Left, environment);
                    if (isError(left)) {
                        return left;
                    }

                    IObject index = Eval(indexNode.Index, environment);
                    if (isError(index)) {
                        return index;
                    }

                    return evalIndexExpression(left, index);

                case NodeTypeMapValue.HashMapValue:
                    return evalHashLiteral((HashLiteral)node, environment);

                case NodeTypeMapValue.ForMapValue:
                    IObject result = evalForLoopLiteral((ForLoopLiteral)node, environment);
                    if (result != null) {
                        return result;
                    }

                    break;
            }

            return null;
        }

        private IObject evalTree(AbstractSyntaxTree tree, Environment environment) {
            IObject result = null;

            /*
                Sometimes we have to keep track of object.ReturnValues for longer
            and can’t unwrap their values on the first encounter. That’s the case
            with block statements:
    
                if (10 > 1) {
                    if (10 > 1) {
                        return 10;
                    }
                    return 1;
                }
    
                This is why this for loop exists. To fix the issue.
                Here we explicitly don’t unwrap the return value and only check the Type() of each evaluation
            result. If it’s object.RETURN_VALUE_OBJ we simply return the *object.ReturnValue, without
            unwrapping its .Value, so it stops execution in a possible outer block statement and bubbles
            up to evalProgram, where it finally gets unwrapped.
            */
            foreach (var statement in tree.Statements) {
                result = Eval(statement, environment);
                if (result != null) {
                    Type resultType = result.GetType();

                    if (resultType == typeof(ReturnValueObj)) {
                        return ((ReturnValueObj)result).Value;
                    } else if (resultType == typeof(ErrorObj)) { // Part of error handling.
                        return result;
                    }
                }
            }

            return result;
        }

        private IObject evalBlockStatement(BlockStatement blockStatement, Environment environment) {
            IObject result = null;
            foreach (var statement in blockStatement.Statements) {
                result = Eval(statement, environment);
                /*
                    Both return and error encounter stops the current statements' execution.
                */
                if (result != null) {
                    string resultType = result.Type();
                    if (resultType == ObjectType.RETURN_VALUE_OBJ || resultType == ObjectType.ERROR_OBJ
                        || resultType == ObjectType.BREAK_OBJ) {
                        return result;
                    }
                }
            }

            return result;
        }

        private IObject evalIncrementExpression(IncrementExpression expression, Environment environment) {
            if (!(expression.Operand is Identifier identifier)) {
                return NewError("++ 的運算元必須是變數名稱。");
            }
            IObject original = environment.Get(identifier.Value, out bool exists);
            if (!exists) return NewError($"Identifier not found: {identifier.Value}");

            IObject incremented;
            switch (original) {
                case IntegerObj integer:
                    incremented = new IntegerObj(integer.Value + 1);
                    break;
                case FloatObj floating:
                    incremented = new FloatObj(floating.Value + 1.0f);
                    break;
                case DoubleObj doubleValue:
                    incremented = new DoubleObj(doubleValue.Value + 1.0d);
                    break;
                default:
                    return NewError($"++ 只適用於數值變數，得到 {original.Type()}。");
            }
            environment.Set(identifier.Value, incremented);
            return expression.IsPrefix ? incremented : original;
        }

        private IObject evalPrefixExpression(string _operator, IObject right) {
            switch (_operator) {
                case "!":
                    return evalBangOperatorExpression(right);
                case "-":
                    return evalMinusPrefixOperatorExpression(right);
                default:
                    return NewError($"Unknown operator: {_operator}{right.Type()}");
            }
        }

        private IObject evalInfixExpression(string _operator, IObject left, IObject right) {
            if (ObjectType.IsNumber(left.Type()) && ObjectType.IsNumber(right.Type())) {
                return evalNumberInfixExpression(_operator, left, right);
            }
            if (left is StringObj leftString && right is StringObj rightString) {
                if (_operator == "==") return nativeBoolToBoolObj(leftString.Value == rightString.Value);
                if (_operator == "!=") return nativeBoolToBoolObj(leftString.Value != rightString.Value);
            }
            if (left is BooleanObj leftBoolean && right is BooleanObj rightBoolean) {
                if (_operator == "==") return nativeBoolToBoolObj(leftBoolean.Value == rightBoolean.Value);
                if (_operator == "!=") return nativeBoolToBoolObj(leftBoolean.Value != rightBoolean.Value);
            }

            switch (_operator) {
                case "==":
                    return nativeBoolToBoolObj(left == right);
                case "!=":
                    return nativeBoolToBoolObj(left != right);
                case "&&":
                    if (left is BooleanObj _left && right is BooleanObj _right) {
                        return nativeBoolToBoolObj(_left.Value && _right.Value);
                    }

                    return NewError(
                        $"Two operands are not both boolean for && operator: {left.Inspect()}{_operator}{right.Inspect()}");
                case "||":
                    if (left is BooleanObj __left && right is BooleanObj __right) {
                        return nativeBoolToBoolObj(__left.Value || __right.Value);
                    }

                    return NewError(
                        $"Two operands are not both boolean for || operator: {left.Inspect()}{_operator}{right.Inspect()}");
            }

            if (left.Type() == ObjectType.STRING_OBJ && right.Type() == ObjectType.STRING_OBJ) {
                return evalStringInfixExpression(_operator, left, right);
            }

            if (left.Type() != right.Type()) {
                return NewError($"Type mismatch: {left.Type()} {_operator} {right.Type()}");
            }

            return NewError($"Unknown operator: {left.Type()} {_operator} {right.Type()}");
        }

        private IObject evalStringInfixExpression(string _operator, IObject left, IObject right) {
            if (_operator != "+") {
                return NewError($"Unknown operator: {left.Type()} {_operator} {right.Type()}");
            }

            string leftVal = ((StringObj)left).Value;
            string rightVal = ((StringObj)right).Value;
            return new StringObj(leftVal + rightVal);
        }

        private IObject evalIfExpression(IfExpression ifExpression, Environment environment) {
            IObject condition = Eval(ifExpression.Condition, environment);
            if (isError(condition)) {
                return condition;
            }

            if (isTruthy(condition)) {
                return Eval(ifExpression.Consequence, environment);
            }

            IExpression[] ifExpressionAlternativeConditions = ifExpression.AlternativeConditions;

            if (ifExpressionAlternativeConditions != null) {
                BlockStatement[] ifExpressionAlternatives = ifExpression.Alternatives;
                for (var i = 0; i < ifExpressionAlternativeConditions.Length; i++) {
                    IObject _condition = Eval(ifExpressionAlternativeConditions[i], environment);
                    if (isError(_condition)) {
                        return _condition;
                    }

                    if (isTruthy(_condition)) {
                        return Eval(ifExpressionAlternatives[i], environment);
                    }
                }
            }

            if (ifExpression.LastResort != null) {
                return Eval(ifExpression.LastResort, environment);
            }

            return RepeatedPrimitives.NULL;
        }

        private IObject[] evalExpressions(IExpression[] expressions, Environment environment) {
            List<IObject> result = new List<IObject>();
            foreach (var expression in expressions) {
                IObject evaluated = Eval(expression, environment);
                if (isError(evaluated)) {
                    return new[] { evaluated };
                }

                result.Add(evaluated);
            }

            return result.ToArray();
        }

        private IObject applyFunction(IObject fn, IObject[] args, Environment environment) {
            if (fn is FunctionObj functionObj) {
                Environment extendedEnv = extendFunctionEnv(functionObj, args);
                IObject evaluated = Eval(functionObj.Body, extendedEnv);
                return unwrapReturnValue(evaluated);
            } else if (fn is BuiltinObj builtinObj) {
                return builtinObj.Fn(args);
            }

            return NewError($"Not a function: {fn.Type()}");
        }

        // private void reassignVariable()

        private IObject evalIdentifier(Identifier node, Environment environment) {
            // Looking up built-in functions.
            bool hasVal = builtins.BuiltinFuncs.TryGetValue(node.Value, out BuiltinObj value);
            if (hasVal) {
                return value;
            }

            // Looking up built-in identifiers.
            hasVal = builtins._Builtins.TryGetValue(node.Value, out IObject _value);
            if (hasVal) {
                return _value;
            }

            IObject val = environment.Get(node.Value, out bool hasVar);
            if (!hasVar) {
                return NewError($"Identifier not found: {node.Value}");
            }

            return val;
        }

        private IObject evalMinusPrefixOperatorExpression(IObject right) {
            if (!ObjectType.IsNumber(right.Type())) {
                return NewError($"Unknown operator: -{right.Type()}");
            }

            double value = ((INumberObj)right).GetNumValue();

            switch (right.Type()) {
                case ObjectType.INTEGER_OBJ:
                    return new IntegerObj((int)-value);
                case ObjectType.FLOAT_OBJ:
                    return new FloatObj((float)-value);
                case ObjectType.DOUBLE_OBJ:
                    return new DoubleObj(-value);
            }

            return null;
        }

        private IObject evalNumberInfixExpression(string _operator, IObject left, IObject right) {
            INumberObj _left = (INumberObj)left;
            INumberObj _right = (INumberObj)right;
            string leftType = left.Type();
            string rightType = right.Type();

            /*
             * Set the number object to return to type of biggest decimal range if one of them (left or right) is that type.
             */
            int type;
            if (leftType == ObjectType.DOUBLE_OBJ || rightType == ObjectType.DOUBLE_OBJ) {
                type = 2;
            } else if (leftType == ObjectType.FLOAT_OBJ || rightType == ObjectType.FLOAT_OBJ) {
                type = 1;
            } else {
                type = 0;
            }

            return _operator switch {
                "+" => type switch {
                    0 => new IntegerObj((int)(_left.GetNumValue() + _right.GetNumValue())),
                    1 => new FloatObj((float)(_left.GetNumValue() + _right.GetNumValue())),
                    2 => new DoubleObj(_left.GetNumValue() + _right.GetNumValue()),
                },
                "-" => type switch {
                    0 => new IntegerObj((int)(_left.GetNumValue() - _right.GetNumValue())),
                    1 => new FloatObj((float)(_left.GetNumValue() - _right.GetNumValue())),
                    2 => new DoubleObj(_left.GetNumValue() - _right.GetNumValue()),
                },
                "*" => type switch {
                    0 => new IntegerObj((int)(_left.GetNumValue() * _right.GetNumValue())),
                    1 => new FloatObj((float)(_left.GetNumValue() * _right.GetNumValue())),
                    2 => new DoubleObj(_left.GetNumValue() * _right.GetNumValue()),
                },
                "/" => type switch {
                    0 => new IntegerObj((int)(_left.GetNumValue() / _right.GetNumValue())),
                    1 => new FloatObj((float)(_left.GetNumValue() / _right.GetNumValue())),
                    2 => new DoubleObj(_left.GetNumValue() / _right.GetNumValue()),
                },
                /*
                 * Comparisons should be able to just all use double to do the operations.
                 */
                "<" => nativeBoolToBoolObj(_left.GetNumValue() < _right.GetNumValue()),
                ">" => nativeBoolToBoolObj(_left.GetNumValue() > _right.GetNumValue()),
                "<=" => nativeBoolToBoolObj(_left.GetNumValue() <= _right.GetNumValue()),
                ">=" => nativeBoolToBoolObj(_left.GetNumValue() >= _right.GetNumValue()),
                /*
                 * TODO Beware of precision problems for double. Find a solution for this in the future.
                 */
                "==" => nativeBoolToBoolObj(_left.GetNumValue() == _right.GetNumValue()),
                "!=" => nativeBoolToBoolObj(_left.GetNumValue() != _right.GetNumValue()),
                _ => NewError($"Unknown operator: {left.Type()} {_operator} {right.Type()}")
            };
        }

        private IObject evalBangOperatorExpression(IObject right) {
            if (right is BooleanObj boolean) return nativeBoolToBoolObj(!boolean.Value);
            if (right == RepeatedPrimitives.TRUE) {
                return RepeatedPrimitives.FALSE;
            } else if (right == RepeatedPrimitives.FALSE) {
                return RepeatedPrimitives.TRUE;
            } else if (right == RepeatedPrimitives.NULL) {
                return RepeatedPrimitives.TRUE;
            }

            return RepeatedPrimitives.FALSE;
        }

        private IObject evalIndexExpression(IObject left, IObject index) {
            if (left.Type() == ObjectType.ARRAY_OBJ && index.Type() == ObjectType.INTEGER_OBJ) {
                return evalArrayIndexExpression(left, index);
            } else if (left.Type() == ObjectType.HASH_OBJ) {
                return evalHashIndexExpression(left, index);
            }

            return NewError($"Index operator not supported: {left.Type()}");
        }

        private IObject evalArrayIndexExpression(IObject array, IObject index) {
            ArrayObj arrayObj = (ArrayObj)array;
            int idx = ((IntegerObj)index).Value;
            int max = arrayObj.Elements.Length - 1;
            if (idx < 0 || idx > max) {
                return RepeatedPrimitives.NULL;
            }

            return arrayObj.Elements[idx];
        }

        private IObject evalHashIndexExpression(IObject hash, IObject index) {
            HashObj hashObj = (HashObj)hash;
            bool ok = Utils.TryCast(index, out IHashable key);
            if (!ok) {
                return NewError($"Unusable as hash key: {index.Type()}");
            }

            ok = hashObj.Pairs.TryGetValue(key.HashKey(), out HashPair pair);

            if (!ok) {
                return RepeatedPrimitives.NULL;
            }

            return pair.Value;
        }

        private IObject evalHashLiteral(HashLiteral node, Environment environment) {
            Dictionary<HashKey, HashPair> pairs = new Dictionary<HashKey, HashPair>();
            foreach (var pair in node.Pairs) {
                IObject key = Eval(pair.Key, environment);
                if (isError(key)) {
                    return key;
                }

                bool usable = Utils.TryCast(key, out IHashable hashKey);
                if (!usable) {
                    return NewError($"Unusable as hash key: {key.Type()}");
                }

                IObject value = Eval(pair.Value, environment);
                if (isError(value)) {
                    return value;
                }

                HashKey hashed = hashKey.HashKey();
                pairs[hashed] = new HashPair(key, value);
            }

            return new HashObj(pairs);
        }

        private IObject evalForLoopLiteral(ForLoopLiteral node, Environment environment) {
            Environment enclosedEnvironment = Environment.NewEnclosedEnvironment(environment);
            LetStatement letStatement = node.DeclareStatement;
            IExpression conditionalExpression = node.ConditionalExpression;
            IStatement valueChangeStatement = node.ValueChangeStatement;
            BlockStatement blockStatement = node.Body;

            if (letStatement != null) {
                IObject letStmtResult = Eval(letStatement, enclosedEnvironment);
                if (isError(letStmtResult)) {
                    return letStmtResult;
                }
            }

            while (true) {
                IObject evalConditionResult = null;
                if (conditionalExpression != null) {
                    evalConditionResult = Eval(conditionalExpression, enclosedEnvironment);
                    if (isError(evalConditionResult)) {
                        return evalConditionResult;
                    }
                }

                if (evalConditionResult == null) {
                    return NewError("No boolean result for for loop condition evaluation. Got=NULL.");
                }

                if (evalConditionResult.Type() != ObjectType.BOOLEAN_OBJ) {
                    return NewError($"Expected bool from for loop conditionals. Got={evalConditionResult.Type()}");
                }

                if (!((BooleanObj)evalConditionResult).Value) {
                    break;
                }

                IObject result = null;
                if (blockStatement != null) {
                    result = Eval(blockStatement, enclosedEnvironment);
                    if (isError(result)) {
                        return result;
                    }
                }

                // Only control-flow values terminate a loop; ordinary expression values are discarded.
                if (result != null && result.Type() == ObjectType.RETURN_VALUE_OBJ) {
                    return result;
                }
                if (result != null && result.Type() == ObjectType.BREAK_OBJ) {
                    break;
                }

                if (valueChangeStatement != null) {
                    IObject valChangeResult = Eval(valueChangeStatement, enclosedEnvironment);
                    if (isError(valChangeResult)) {
                        return valChangeResult;
                    }
                }

                string letStatementVarName = letStatement.Name.Value;
                IObject letStatementVar = enclosedEnvironment.Get(letStatementVarName, out bool hasVar);
                enclosedEnvironment = Environment.NewEnclosedEnvironment(environment);

                /*
                 * Value saved to variable of which was declared inside parentheses (ex. "func (let i = 0;...") of for loop,
                 * should have its value kept during each loop. Other variables declared inside body ({}) should be cleared out. 
                 */
                enclosedEnvironment.Create(letStatementVarName, letStatementVar);
            }

            return null;
        }

        /// <summary>
        ///     Create a new environment (local scope) for inside the function. Values of variables
        /// from parameters get passed into this newly created environment.
        ///
        ///     Extending the function’s environment and not the current environment also makes it
        /// possible to return closures:
        ///
        /// >> let newAdder = fn(x) { fn(y) { x + y } };
        /// >> let addThree = newAdder(3);
        /// >> addThree(10);
        /// 13
        ///
        ///     newAdder here is a higher-order function. Higher-order functions are functions that either return
        /// other functions or receive them as arguments. In this case newAdder returns another function.
        /// But not just any function: a closure. addThree is bound to the closure that’s returned when calling
        /// newAdder with 3 as the sole argument.
        ///
        ///     When addThree is called it not only has access to the arguments of the call, the y parameter, but
        /// it can also reach the value x was bound to at the time of the newAdder(2) call, the closure addThree
        /// still has access to the environment that was the current environment at the time of its definition.
        /// Which is when the last line of newAdder’s body was evaluated.
        /// </summary>
        /// <param name="fn"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private Environment extendFunctionEnv(FunctionObj fn, IObject[] args) {
            Environment environment = Environment.NewEnclosedEnvironment(fn.Env);

            for (var i = 0; i < fn.Parameters.Length; i++) {
                environment.Create(fn.Parameters[i].Value, args[i]);
            }

            return environment;
        }

        private IObject unwrapReturnValue(IObject obj) {
            /*
             This is to stop executing any further statements after the return expression.
             Just return back the unwrapped value.
            */
            if (obj is ReturnValueObj returnValueObj) {
                return returnValueObj.Value;
            }

            return obj;
        }

        private BooleanObj nativeBoolToBoolObj(bool input) {
            return input ? RepeatedPrimitives.TRUE : RepeatedPrimitives.FALSE;
        }

        /// <summary>
        /// Create new *object.Errors and return them when encountering error in script.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns></returns>
        public ErrorObj NewError(string msg) {
            return new ErrorObj(msg);
        }

        /// <summary>
        ///     We need to check for errors whenever we call Eval inside of Eval, in order to stop
        /// errors from being passed around and then bubbling up far away from their origin.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        private bool isError(IObject obj) {
            if (obj != null) {
                return obj.Type() == ObjectType.ERROR_OBJ;
            }

            return false;
        }

        private bool isTruthy(IObject obj) {
            if (obj is BooleanObj boolean) return boolean.Value;
            if (obj == RepeatedPrimitives.NULL) {
                return false;
            } else if (obj == RepeatedPrimitives.TRUE) {
                return true;
            } else if (obj == RepeatedPrimitives.FALSE) {
                return false;
            }

            return true;
        }

        private Dictionary<Type, NodeTypeMapValue> nodeTypeMap = new Dictionary<Type, NodeTypeMapValue>() {
            { typeof(AbstractSyntaxTree), NodeTypeMapValue.ASTMapValue },
            { typeof(BlockStatement), NodeTypeMapValue.BlockMapValue },
            { typeof(ExpressionStatement), NodeTypeMapValue.ExpressMapValue },
            { typeof(ReturnStatement), NodeTypeMapValue.ReturnMapValue },
            { typeof(BreakStatement), NodeTypeMapValue.BreakMapValue },
            { typeof(LetStatement), NodeTypeMapValue.LetMapValue },
            { typeof(IntegerLiteral), NodeTypeMapValue.IntMapValue },
            { typeof(FloatLiteral), NodeTypeMapValue.FloatMapValue },
            { typeof(DoubleLiteral), NodeTypeMapValue.DoubleMapValue },
            { typeof(BooleanLiteral), NodeTypeMapValue.BoolMapValue },
            { typeof(ForLoopLiteral), NodeTypeMapValue.ForMapValue },
            { typeof(PrefixExpression), NodeTypeMapValue.PrefixMapValue },
            { typeof(IncrementExpression), NodeTypeMapValue.IncrementMapValue },
            { typeof(InfixExpression), NodeTypeMapValue.InfixMapValue },
            { typeof(IfExpression), NodeTypeMapValue.IfMapValue },
            { typeof(Identifier), NodeTypeMapValue.IdentMapValue },
            { typeof(FunctionLiteral), NodeTypeMapValue.FuncMapValue },
            { typeof(CallExpression), NodeTypeMapValue.CallMapValue },
            { typeof(StringLiteral), NodeTypeMapValue.StringMapValue },
            { typeof(ArrayLiteral), NodeTypeMapValue.ArrayMapValue },
            { typeof(IndexExpression), NodeTypeMapValue.IndexMapValue },
            { typeof(HashLiteral), NodeTypeMapValue.HashMapValue },
        };

        /// <summary>
        /// For use to compare types using switch through the help of dictionary and constant enum values.
        /// </summary>
        private enum NodeTypeMapValue {
            ASTMapValue,
            BlockMapValue,
            ExpressMapValue,
            ReturnMapValue,
            BreakMapValue,
            LetMapValue,
            IntMapValue,
            FloatMapValue,
            DoubleMapValue,
            BoolMapValue,
            PrefixMapValue,
            IncrementMapValue,
            InfixMapValue,
            IfMapValue,
            IdentMapValue,
            FuncMapValue,
            CallMapValue,
            StringMapValue,
            ArrayMapValue,
            IndexMapValue,
            HashMapValue,
            ForMapValue,
        }
    }
}
