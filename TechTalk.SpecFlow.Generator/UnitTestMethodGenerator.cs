﻿using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using Gherkin.Ast;
using TechTalk.SpecFlow.Generator.CodeDom;
using TechTalk.SpecFlow.Generator.Generation;
using TechTalk.SpecFlow.Generator.UnitTestConverter;
using TechTalk.SpecFlow.Generator.UnitTestProvider;
using TechTalk.SpecFlow.Parser;
using TechTalk.SpecFlow.Tracing;

namespace TechTalk.SpecFlow.Generator
{
    public class UnitTestMethodGenerator
    {
        private const string IGNORE_TAG = "@Ignore";
        private const string TESTRUNNER_FIELD = "testRunner";
        private readonly CodeDomHelper _codeDomHelper;
        private readonly IDecoratorRegistry _decoratorRegistry;
        private readonly LinePragmaHandler _linePragmaHandler;
        private readonly ScenarioPartHelper _scenarioPartHelper;
        private readonly IUnitTestGeneratorProvider _unitTestGeneratorProvider;

        public UnitTestMethodGenerator(IUnitTestGeneratorProvider unitTestGeneratorProvider, IDecoratorRegistry decoratorRegistry, CodeDomHelper codeDomHelper, LinePragmaHandler linePragmaHandler,
            ScenarioPartHelper scenarioPartHelper)
        {
            _unitTestGeneratorProvider = unitTestGeneratorProvider;
            _decoratorRegistry = decoratorRegistry;
            _codeDomHelper = codeDomHelper;
            _linePragmaHandler = linePragmaHandler;
            _scenarioPartHelper = scenarioPartHelper;
        }

        public void CreateUnitTests(SpecFlowFeature feature, TestClassGenerationContext generationContext)
        {
            foreach (var scenarioDefinition in feature.ScenarioDefinitions)
            {
                CreateUnitTest(feature, generationContext, scenarioDefinition);
            }
        }

        private void CreateUnitTest(SpecFlowFeature feature, TestClassGenerationContext generationContext, StepsContainer scenarioDefinition)
        {
            if (string.IsNullOrEmpty(scenarioDefinition.Name))
            {
                throw new TestGeneratorException("The scenario must have a title specified.");
            }

            if (scenarioDefinition is ScenarioOutline scenarioOutline)
            {
                GenerateScenarioOutlineTest(generationContext, scenarioOutline, feature);
            }
            else
            {
                GenerateTest(generationContext, (Scenario) scenarioDefinition, feature);
            }
        }

        private void GenerateScenarioOutlineTest(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline, SpecFlowFeature feature)
        {
            ValidateExampleSetConsistency(scenarioOutline);

            var paramToIdentifier = CreateParamToIdentifierMapping(scenarioOutline);

            var scenarioOutlineTestMethod = CreateScenatioOutlineTestMethod(generationContext, scenarioOutline, paramToIdentifier);
            var exampleTagsParam = new CodeVariableReferenceExpression(GeneratorConstants.SCENARIO_OUTLINE_EXAMPLE_TAGS_PARAMETER);
            if (generationContext.GenerateRowTests)
            {
                GenerateScenarioOutlineExamplesAsRowTests(generationContext, scenarioOutline, scenarioOutlineTestMethod);
            }
            else
            {
                GenerateScenarioOutlineExamplesAsIndividualMethods(scenarioOutline, generationContext, scenarioOutlineTestMethod, paramToIdentifier);
            }

            GenerateTestBody(generationContext, scenarioOutline, scenarioOutlineTestMethod, feature, exampleTagsParam, paramToIdentifier);
        }

        private void GenerateTest(TestClassGenerationContext generationContext, Scenario scenario, SpecFlowFeature feature)
        {
            var testMethod = CreateTestMethod(generationContext, scenario, null);
            GenerateTestBody(generationContext, scenario, testMethod, feature);
        }

        private void ValidateExampleSetConsistency(ScenarioOutline scenarioOutline)
        {
            if (scenarioOutline.Examples.Count() <= 1)
            {
                return;
            }

            var firstExamplesHeader = scenarioOutline.Examples.First().TableHeader.Cells.Select(c => c.Value).ToArray();

            //check params
            if (scenarioOutline.Examples
                .Skip(1)
                .Select(examples => examples.TableHeader.Cells.Select(c => c.Value))
                .Any(paramNames => !paramNames.SequenceEqual(firstExamplesHeader)))
            {
                throw new TestGeneratorException("The example sets must provide the same parameters.");
            }
        }

        private IEnumerable<string> GetNonIgnoreTags(IEnumerable<Tag> tags)
        {
            return tags.Where(t => !t.Name.Equals(IGNORE_TAG, StringComparison.InvariantCultureIgnoreCase)).Select(t => t.GetNameWithoutAt());
        }

        private bool HasIgnoreTag(IEnumerable<Tag> tags)
        {
            return tags.Any(t => t.Name.Equals(IGNORE_TAG, StringComparison.InvariantCultureIgnoreCase));
        }

        private void GenerateTestBody(
            TestClassGenerationContext generationContext,
            StepsContainer scenario,
            CodeMemberMethod testMethod,
            SpecFlowFeature feature,
            CodeExpression additionalTagsExpression = null, ParameterSubstitution paramToIdentifier = null)
        {
            //call test setup
            //ScenarioInfo scenarioInfo = new ScenarioInfo("xxxx", tags...);
            CodeExpression tagsExpression;
            if (additionalTagsExpression == null)
            {
                tagsExpression = _scenarioPartHelper.GetStringArrayExpression(scenario.GetTags());
            }
            else if (!scenario.HasTags())
            {
                tagsExpression = additionalTagsExpression;
            }
            else
            {
                // merge tags list
                // var tags = tags1
                // if (tags2 != null)
                //   tags = Enumerable.ToArray(Enumerable.Concat(tags1, tags1));
                testMethod.Statements.Add(
                    new CodeVariableDeclarationStatement(typeof(string[]), "__tags", _scenarioPartHelper.GetStringArrayExpression(scenario.GetTags())));
                tagsExpression = new CodeVariableReferenceExpression("__tags");
                testMethod.Statements.Add(
                    new CodeConditionStatement(
                        new CodeBinaryOperatorExpression(
                            additionalTagsExpression,
                            CodeBinaryOperatorType.IdentityInequality,
                            new CodePrimitiveExpression(null)),
                        new CodeAssignStatement(
                            tagsExpression,
                            new CodeMethodInvokeExpression(
                                new CodeTypeReferenceExpression(typeof(Enumerable)),
                                "ToArray",
                                new CodeMethodInvokeExpression(
                                    new CodeTypeReferenceExpression(typeof(Enumerable)),
                                    "Concat",
                                    tagsExpression,
                                    additionalTagsExpression)))));
            }

            testMethod.Statements.Add(
                new CodeVariableDeclarationStatement(typeof(ScenarioInfo), "scenarioInfo",
                    new CodeObjectCreateExpression(typeof(ScenarioInfo),
                        new CodePrimitiveExpression(scenario.Name),
                        new CodePrimitiveExpression(scenario.Description),
                        tagsExpression)));

            GenerateScenarioInitializeCall(generationContext, scenario, testMethod);

            GenerateTestMethodBody(generationContext, scenario, testMethod, paramToIdentifier, feature);

            GenerateScenarioCleanupMethodCall(generationContext, testMethod);
        }

        internal void GenerateTestMethodBody(TestClassGenerationContext generationContext, StepsContainer scenario, CodeMemberMethod testMethod, ParameterSubstitution paramToIdentifier,
            SpecFlowFeature feature)
        {
            if (IsIgnoredFeature(feature) || IsIgnoredStepsContainer(scenario))
            {
                AddTestRunnerSkipScenarioCall(testMethod);
            }
            else
            {
                GenerateScenarioStartMethodCall(generationContext, testMethod);
                GenerateMethodBodyForNotSkippedScenarios(generationContext, scenario, testMethod, paramToIdentifier);
            }
        }

        internal bool IsIgnoredFeature(SpecFlowFeature specFlowFeature)
        {
            return specFlowFeature.Tags.Any(t => string.Equals(t.Name, IGNORE_TAG, StringComparison.InvariantCultureIgnoreCase));
        }

        internal bool IsIgnoredStepsContainer(StepsContainer stepsContainer)
        {
            return stepsContainer.GetTags().Any(t => string.Equals(t.Name, IGNORE_TAG, StringComparison.InvariantCultureIgnoreCase));
        }

        internal void GenerateScenarioStartMethodCall(TestClassGenerationContext generationContext, CodeMemberMethod testMethod)
        {
            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    generationContext.ScenarioStartMethod.Name));
        }

        internal void GenerateScenarioInitializeCall(TestClassGenerationContext generationContext, StepsContainer scenario, CodeMemberMethod testMethod)
        {
            _linePragmaHandler.AddLineDirective(testMethod.Statements, scenario);
            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    generationContext.ScenarioInitializeMethod.Name,
                    new CodeVariableReferenceExpression("scenarioInfo")));
        }

        internal void GenerateScenarioCleanupMethodCall(TestClassGenerationContext generationContext, CodeMemberMethod testMethod)
        {
            _linePragmaHandler.AddLineDirectiveHidden(testMethod.Statements);

            // call scenario cleanup
            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    generationContext.ScenarioCleanupMethod.Name));
        }

        internal void GenerateMethodBodyForNotSkippedScenarios(TestClassGenerationContext generationContext, StepsContainer scenario, CodeMemberMethod testMethod,
            ParameterSubstitution paramToIdentifier)
        {
            if (generationContext.Feature.HasFeatureBackground())
            {
                _linePragmaHandler.AddLineDirective(testMethod.Statements, generationContext.Feature.Background);
                testMethod.Statements.Add(
                    new CodeMethodInvokeExpression(
                        new CodeThisReferenceExpression(),
                        generationContext.FeatureBackgroundMethod.Name));
            }

            //var ifStatement = new CodeConditionStatement(new CodeExpression(), new CodeStatement[] { new CodeExpressionStatement(CreateTestRunnerSkipScenarioCall()) }, new CodeStatement[]{});

            //testMethod.Statements.Add(ifStatement);


            foreach (var scenarioStep in scenario.Steps)
            {
                _scenarioPartHelper.GenerateStep(testMethod, scenarioStep, paramToIdentifier);
            }
        }

        internal void AddTestRunnerSkipScenarioCall(CodeMemberMethod testMethod)
        {
            testMethod.Statements.Add(
                CreateTestRunnerSkipScenarioCall());
        }

        private CodeMethodInvokeExpression CreateTestRunnerSkipScenarioCall()
        {
            return new CodeMethodInvokeExpression(
                new CodeFieldReferenceExpression(null, TESTRUNNER_FIELD),
                nameof(TestRunner.SkipScenario));
        }

        private void GenerateScenarioOutlineExamplesAsIndividualMethods(
            ScenarioOutline scenarioOutline,
            TestClassGenerationContext generationContext,
            CodeMemberMethod scenarioOutlineTestMethod, ParameterSubstitution paramToIdentifier)
        {
            var exampleSetIndex = 0;

            foreach (var exampleSet in scenarioOutline.Examples)
            {
                var useFirstColumnAsName = CanUseFirstColumnAsName(exampleSet.TableBody);
                string exampleSetIdentifier;

                if (string.IsNullOrEmpty(exampleSet.Name))
                {
                    if (scenarioOutline.Examples.Count(es => string.IsNullOrEmpty(es.Name)) > 1)
                    {
                        exampleSetIdentifier = $"ExampleSet {exampleSetIndex}".ToIdentifier();
                    }
                    else
                    {
                        exampleSetIdentifier = null;
                    }
                }
                else
                {
                    exampleSetIdentifier = exampleSet.Name.ToIdentifier();
                }


                foreach (var example in exampleSet.TableBody.Select((r, i) => new {Row = r, Index = i}))
                {
                    var variantName = useFirstColumnAsName ? example.Row.Cells.First().Value : string.Format("Variant {0}", example.Index);
                    GenerateScenarioOutlineTestVariant(generationContext, scenarioOutline, scenarioOutlineTestMethod, paramToIdentifier, exampleSet.Name ?? "", exampleSetIdentifier, example.Row, exampleSet.Tags, variantName);
                }

                exampleSetIndex++;
            }
        }

        private void GenerateScenarioOutlineExamplesAsRowTests(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline, CodeMemberMethod scenatioOutlineTestMethod)
        {
            SetupTestMethod(generationContext, scenatioOutlineTestMethod, scenarioOutline, null, null, null, true);

            foreach (var examples in scenarioOutline.Examples)
            {
                foreach (var row in examples.TableBody)
                {
                    var arguments = row.Cells.Select(c => c.Value);
                    _unitTestGeneratorProvider.SetRow(generationContext, scenatioOutlineTestMethod, arguments, GetNonIgnoreTags(examples.Tags), HasIgnoreTag(examples.Tags));
                }
            }
        }

        private ParameterSubstitution CreateParamToIdentifierMapping(ScenarioOutline scenarioOutline)
        {
            var paramToIdentifier = new ParameterSubstitution();
            foreach (var param in scenarioOutline.Examples.First().TableHeader.Cells)
            {
                paramToIdentifier.Add(param.Value, param.Value.ToIdentifierCamelCase());
            }

            return paramToIdentifier;
        }


        private bool CanUseFirstColumnAsName(IEnumerable<Gherkin.Ast.TableRow> tableBody)
        {
            if (tableBody.Any(r => !r.Cells.Any()))
            {
                return false;
            }

            return tableBody.Select(r => r.Cells.First().Value.ToIdentifier()).Distinct().Count() == tableBody.Count();
        }

        private CodeMemberMethod CreateScenatioOutlineTestMethod(TestClassGenerationContext generationContext, ScenarioOutline scenarioOutline, ParameterSubstitution paramToIdentifier)
        {
            var testMethod = _codeDomHelper.CreateMethod(generationContext.TestClass);

            testMethod.Attributes = MemberAttributes.Public;
            testMethod.Name = string.Format(GeneratorConstants.TEST_NAME_FORMAT, scenarioOutline.Name.ToIdentifier());

            foreach (var pair in paramToIdentifier)
            {
                testMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof(string), pair.Value));
            }

            testMethod.Parameters.Add(new CodeParameterDeclarationExpression(typeof(string[]), GeneratorConstants.SCENARIO_OUTLINE_EXAMPLE_TAGS_PARAMETER));
            return testMethod;
        }

        private void GenerateScenarioOutlineTestVariant(
            TestClassGenerationContext generationContext,
            ScenarioOutline scenarioOutline,
            CodeMemberMethod scenatioOutlineTestMethod,
            IEnumerable<KeyValuePair<string, string>> paramToIdentifier,
            string exampleSetTitle,
            string exampleSetIdentifier,
            Gherkin.Ast.TableRow row,
            IEnumerable<Tag> exampleSetTags,
            string variantName)
        {
            var testMethod = CreateTestMethod(generationContext, scenarioOutline, exampleSetTags, variantName, exampleSetIdentifier);
            _linePragmaHandler.AddLineDirective(testMethod.Statements, scenarioOutline);

            //call test implementation with the params
            var argumentExpressions = row.Cells.Select(paramCell => new CodePrimitiveExpression(paramCell.Value)).Cast<CodeExpression>().ToList();

            argumentExpressions.Add(_scenarioPartHelper.GetStringArrayExpression(exampleSetTags));

            testMethod.Statements.Add(
                new CodeMethodInvokeExpression(
                    new CodeThisReferenceExpression(),
                    scenatioOutlineTestMethod.Name,
                    argumentExpressions.ToArray()));

            _linePragmaHandler.AddLineDirectiveHidden(testMethod.Statements);
            var arguments = paramToIdentifier.Select((p2i, paramIndex) => new KeyValuePair<string, string>(p2i.Key, row.Cells.ElementAt(paramIndex).Value)).ToList();
            _unitTestGeneratorProvider.SetTestMethodAsRow(generationContext, testMethod, scenarioOutline.Name, exampleSetTitle, variantName, arguments);
        }

        private CodeMemberMethod CreateTestMethod(
            TestClassGenerationContext generationContext,
            StepsContainer scenario,
            IEnumerable<Tag> additionalTags,
            string variantName = null,
            string exampleSetIdentifier = null)
        {
            var testMethod = _codeDomHelper.CreateMethod(generationContext.TestClass);

            SetupTestMethod(generationContext, testMethod, scenario, additionalTags, variantName, exampleSetIdentifier);

            return testMethod;
        }


        private void SetupTestMethod(
            TestClassGenerationContext generationContext,
            CodeMemberMethod testMethod,
            StepsContainer scenarioDefinition,
            IEnumerable<Tag> additionalTags,
            string variantName,
            string exampleSetIdentifier,
            bool rowTest = false)
        {
            testMethod.Attributes = MemberAttributes.Public;
            testMethod.Name = GetTestMethodName(scenarioDefinition, variantName, exampleSetIdentifier);
            var friendlyTestName = scenarioDefinition.Name;
            if (variantName != null)
            {
                friendlyTestName = $"{scenarioDefinition.Name}: {variantName}";
            }

            if (rowTest)
            {
                _unitTestGeneratorProvider.SetRowTest(generationContext, testMethod, friendlyTestName);
            }
            else
            {
                _unitTestGeneratorProvider.SetTestMethod(generationContext, testMethod, friendlyTestName);
            }

            _decoratorRegistry.DecorateTestMethod(generationContext, testMethod, ConcatTags(scenarioDefinition.GetTags(), additionalTags), out var scenarioCategories);

            if (scenarioCategories.Any())
            {
                _unitTestGeneratorProvider.SetTestMethodCategories(generationContext, testMethod, scenarioCategories);
            }
        }

        private static string GetTestMethodName(StepsContainer scenario, string variantName, string exampleSetIdentifier)
        {
            var methodName = string.Format(GeneratorConstants.TEST_NAME_FORMAT, scenario.Name.ToIdentifier());
            if (variantName == null)
            {
                return methodName;
            }

            var variantNameIdentifier = variantName.ToIdentifier().TrimStart('_');
            methodName = string.IsNullOrEmpty(exampleSetIdentifier)
                ? $"{methodName}_{variantNameIdentifier}"
                : $"{methodName}_{exampleSetIdentifier}_{variantNameIdentifier}";

            return methodName;
        }

        private IEnumerable<Tag> ConcatTags(params IEnumerable<Tag>[] tagLists)
        {
            return tagLists.Where(tagList => tagList != null).SelectMany(tagList => tagList);
        }
    }
}