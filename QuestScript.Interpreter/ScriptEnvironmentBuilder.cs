﻿using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using QuestScript.Interpreter.Exceptions;
using QuestScript.Interpreter.Extensions;
using QuestScript.Interpreter.Helpers;
using QuestScript.Interpreter.InterpreterElements;
using QuestScript.Parser;
using QuestScript.Parser.ScriptElements;
using Environment = QuestScript.Interpreter.InterpreterElements.Environment;

// ReSharper disable ArgumentsStyleLiteral

namespace QuestScript.Interpreter
{
    public sealed class ScriptEnvironmentBuilder : QuestScriptBaseVisitor<bool>
    {
        private readonly Dictionary<ParserRuleContext, Environment> _environmentsByContext =
            new Dictionary<ParserRuleContext, Environment>();

        private Environment _current;
        private Environment _root;
        private ScriptEnvironment _scriptEnvironment;

        public ScriptEnvironmentBuilder()
        {
            TypeInferenceVisitor = new TypeInferenceVisitor(this);
            ValueResolverVisitor = new ValueResolverVisitor(this);
        }

        public TypeInferenceVisitor TypeInferenceVisitor { get; }

        public ValueResolverVisitor ValueResolverVisitor { get; }

        public HashSet<BaseInterpreterException> Errors { get; } = new HashSet<BaseInterpreterException>();

        public ScriptEnvironment Output =>
            _scriptEnvironment ?? (_scriptEnvironment = new ScriptEnvironment(_root, _environmentsByContext));

        public override bool Visit(IParseTree tree)
        {
            base.Visit(tree);
            return Errors.Count == 0;
        }

        public override bool VisitScript(QuestScriptParser.ScriptContext context)
        {
            _root = new Environment
            {
                Context = context
            };
            _current = _root;
            return base.VisitScript(context);
        }

        public override bool VisitCodeBlockStatement(QuestScriptParser.CodeBlockStatementContext context)
        {
            _current = _current.CreateChild(context); //push
            var success = base.VisitCodeBlockStatement(context);
            _current = _current.Parent; //pop
            return success;
        }

        public override bool VisitWhileStatement(QuestScriptParser.WhileStatementContext context)
        {
            //if while has a code block, no need for opening a scope
            var shouldOpenNewScope = !context.code.HasDescendantOfType<QuestScriptParser.CodeBlockStatementContext>();
            if (shouldOpenNewScope) _current = _current.CreateChild(context); //push
            var success = base.VisitWhileStatement(context);

            if (shouldOpenNewScope) _current = _current.Parent; //pop

            return success;
        }

        public override bool VisitForStatement(QuestScriptParser.ForStatementContext context)
        {
            _current = _current.CreateChild(context); //push

            if (!_current.IsVariableDefined(context.iterationVariable.Text))
                DeclareLocalVariable(context.iterationVariable.Text, context, ObjectType.Integer,
                    ValueResolverVisitor.Visit(context.iterationStart), isIterationVariable: true);
            else
                Errors.Add(
                    new ConflictingVariableName(context, context.iterationVariable.Text,
                        "Iteration variable names in 'for' statements must not conflict with already defined variables."));

            var success = base.VisitForStatement(context);
            _current = _current.Parent; //pop
            return success;
        }

        public override bool VisitForEachStatement(QuestScriptParser.ForEachStatementContext context)
        {
            _current = _current.CreateChild(context); //push
            var enumerationVariableType = TypeInferenceVisitor.Visit(context.enumerationVariable);
            if (enumerationVariableType != ObjectType.List)
                Errors.Add(new UnexpectedTypeException(context, ObjectType.List, enumerationVariableType,
                    context.enumerationVariable, "'foreach' can only enumerate on collection types."));

            if (!_current.IsVariableDefined(context.iterationVariable.Text))
                DeclareLocalVariable(context.iterationVariable.Text, context, context.enumerationVariable,
                    isEnumerationVariable: true);
            else
                Errors.Add(
                    new ConflictingVariableName(context, context.iterationVariable.Text,
                        "Iteration variable names in 'foreach' statements must not conflict with already defined variables."));

            var success = base.VisitForEachStatement(context);

            _current = _current.Parent; //pop
            return success;
        }

        public override bool VisitStatement(QuestScriptParser.StatementContext context)
        {
            //assign relevant statement to environment, so we can later resolve variables
            //and check if they are used before they are defined
            _current.Statements.Add(context);
            _environmentsByContext.Add(context, _current);
            return base.VisitStatement(context);
        }

        //TODO : add resolution to object members as well                
        public override bool VisitIdentifierOperand(QuestScriptParser.IdentifierOperandContext context)
        {
            //if the parent is RValue -> it is an identifier that is part of an expression, and not a statement
            if (context.HasParentOfType<QuestScriptParser.RValueContext>() &&
                !_current.IsVariableDefined(context.GetText()))
                RecordErrorIfUndefined(context, context.GetText());

            return base.VisitIdentifierOperand(context);
        }

        public override bool VisitAssignmentStatement(QuestScriptParser.AssignmentStatementContext context)
        {
            var success = base.VisitAssignmentStatement(context);

            var identifier = context.LVal.GetText();
            var variable = _current.GetVariable(identifier);
            var variableDefined = variable != null;
            var isMemberAssignment = identifier.Contains(".");

            if (!isMemberAssignment && !variableDefined)
            {
                DeclareLocalVariable(identifier, context.LVal, context.RVal);
            }
            else if (variableDefined && !isMemberAssignment) //do a type check, since we are not declaring but assigning
            {
                //do type checking, since this is not a declaration but an assignment
                var rValueType = TypeInferenceVisitor.Visit(context.RVal);
                var lValueType = TypeInferenceVisitor.Visit(context.LVal);
                if (lValueType != rValueType && !TypeUtil.CanConvert(rValueType, lValueType))
                    Errors.Add(new UnexpectedTypeException(context, lValueType, rValueType, context.RVal,
                        "Also, tried to find suitable implicit casting, but didn't find anything."));

                variable.Value = ValueResolverVisitor.Visit(context.RVal);
            }

            return success;
        }

        //if condition type check - make sure it resolves to boolean type
        public override bool VisitIfStatement(QuestScriptParser.IfStatementContext context)
        {
            var ifConditionExpressionType = TypeInferenceVisitor.Visit(context.condition);
            if (ifConditionExpressionType != ObjectType.Unknown &&
                ifConditionExpressionType != ObjectType.Boolean)
                Errors.Add(new InvalidConditionException(context, "if", context.condition));

            foreach (var elseifCondition in context._elseifConditions)
            {
                var elseIfConditionExpressionType = TypeInferenceVisitor.Visit(elseifCondition);
                if (elseIfConditionExpressionType != ObjectType.Unknown &&
                    elseIfConditionExpressionType != ObjectType.Boolean)
                    Errors.Add(new InvalidConditionException(context, "elseif", elseifCondition));
            }

            return base.VisitIfStatement(context);
        }

        private void DeclareLocalVariable(string name, ParserRuleContext variableContext, ObjectType type,
            Lazy<object> valueResolver, bool isEnumerationVariable = false, bool isIterationVariable = false)
        {
            _current.LocalVariables.Add(new Variable
            {
                Name = name,
                Type = type,
                Value = valueResolver,
                IsEnumerationVariable = isEnumerationVariable,
                IsIterationVariable = isIterationVariable, //if true, prevent changes to the variable
                Context = variableContext
            });
        }

        private void DeclareLocalVariable(string name, ParserRuleContext variableContext,
            ParserRuleContext valueContext, bool isEnumerationVariable = false, bool isIterationVariable = false)
        {
            var type = TypeInferenceVisitor.Visit(valueContext);
            _current.LocalVariables.Add(new Variable
            {
                Name = name,
                Type = type,
                IsEnumerationVariable = isEnumerationVariable,
                IsIterationVariable = isIterationVariable,
                Context = variableContext,
                Value = ValueResolverVisitor.Visit(valueContext)
            });
        }

        //to be used by TypeInferenceVisitor
        internal Variable GetVariableFromCurrentEnvironment(string name)
        {
            return _current.GetVariable(name);
        }

        private void RecordErrorIfUndefined(ParserRuleContext context, string variable)
        {
            if (!_current.IsVariableDefined(variable))
                Errors.Add(new UnresolvedVariableException(variable, context));
        }
    }
}