﻿//
//   Copyright © 2010 Michael Feingold
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using Boo.Lang.Compiler.Ast;
using Boo.Lang.Parser;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text;
using Hill30.BooProject.LanguageService.Colorizer;
using Boo.Lang.Compiler;
using Hill30.BooProject.LanguageService.Mapping.Nodes;

namespace Hill30.BooProject.LanguageService.Mapping
{
    public class NodeMap
    {

        private readonly List<ClassificationSpan> classificationSpans = new List<ClassificationSpan>();
        private readonly BooLanguageService service;
        private readonly BufferMap bufferMap;
        private readonly List<NodeCluster> nodeClusters = new List<NodeCluster>();
        private readonly List<MappedTypeDefinition> types = new List<MappedTypeDefinition>();

        public CompilerErrorCollection Errors { get; private set; }
        public CompilerWarningCollection Warnings { get; private set; }
        public IList<ClassificationSpan> ClassificationSpans { get { return classificationSpans; } }

        public NodeMap(BooLanguageService service, BufferMap bufferMap)
        {
            this.service = service;
            this.bufferMap = bufferMap;
            Errors = new CompilerErrorCollection();
            Warnings = new CompilerWarningCollection();
        }

        internal void Initialize()
        {
            nodeClusters.Clear();
            types.Clear();
            classificationSpans.Clear();
            Errors.Clear();
            Warnings.Clear();
        }

        private antlr.IToken nextToken(antlr.TokenStream tokens)
        {
            while (true)
                try { return tokens.nextToken(); }
// ReSharper disable EmptyGeneralCatchClause
                catch {}
// ReSharper restore EmptyGeneralCatchClause
        }

        internal void MapTokens(antlr.TokenStream tokens)
        {
            antlr.IToken token;
            var currentPos = 0;

            while ((token = nextToken(tokens)).Type != BooLexer.EOF)
            {
                int length;

                switch (token.Type)
                {
                    case BooLexer.INDENT:
                    case BooLexer.DEDENT:
                    case BooLexer.EOL:
                        continue;
                    case BooLexer.SINGLE_QUOTED_STRING:
                    case BooLexer.DOUBLE_QUOTED_STRING:
                        length = token.getText().Length + 2;
                        break;
                    case BooLexer.TRIPLE_QUOTED_STRING:
                        length = token.getText().Length + 6;
                        break;
                    default:
                        length = token.getText().Length;
                        break;
                }

                var start = bufferMap.LocationToPoint(token.getLine(), token.getColumn());
                var end = bufferMap.LocationToPoint(token.getLine(), token.getColumn() + length);
                length = end.Column - start.Column;

                Map(start, length, token);

                var span =
                    new SnapshotSpan(bufferMap.CurrentSnapshot,
                        bufferMap.CurrentSnapshot.GetLineFromLineNumber(token.getLine() - 1).Start + start.Column,
                        length);

                if (span.Start > currentPos)
                    classificationSpans.Add(new ClassificationSpan
                        (new SnapshotSpan(bufferMap.CurrentSnapshot, currentPos, span.Start - currentPos),
                        service.ClassificationTypeRegistry.GetClassificationType(Formats.BooBlockComment)
                        ));

                currentPos = span.End;
            }

            if (currentPos < bufferMap.CurrentSnapshot.Length - 1)
                classificationSpans.Add(
                    new ClassificationSpan(
                        new SnapshotSpan(bufferMap.CurrentSnapshot, currentPos, bufferMap.CurrentSnapshot.Length - currentPos),
                        service.ClassificationTypeRegistry.GetClassificationType(Formats.BooBlockComment)
                        ));
        }

        private int IndexOfBufferPoint(int line, int column)
        {
            return line * bufferMap.LineSize + column;
        }

        private int Lookup(int index)
        {
            var i = nodeClusters.BinarySearch(new NodeCluster(index));
            if (i >= 0)
                return i;
            return Math.Max(0, (~i) - 1);
        }

        private void Map(BufferMap.BufferPoint start, int length, antlr.IToken token)
        {
            var cluster = new NodeCluster(bufferMap.LineSize, start, length, token);
            if (nodeClusters.Count > 0
                && nodeClusters[nodeClusters.Count() - 1].Index >= cluster.Index)
                throw new ArgumentException("Token Mapping order");
            nodeClusters.Add(cluster);
        }

        private void ClustersForNode(MappedNode node, Action<NodeCluster> action)
        {
            var startIndex = IndexOfBufferPoint(node.TextSpan.iStartLine, node.TextSpan.iStartIndex);
            var endIndex = IndexOfBufferPoint(node.TextSpan.iEndLine, node.TextSpan.iEndIndex);
            for (var i = Lookup(startIndex); i < nodeClusters.Count && nodeClusters[i].Index + nodeClusters[i].Length <= endIndex; i++)
                action(nodeClusters[i]);
        }

        public void MapParsedNode(MappedNode node)
        {
            if (node.Type == MappedNodeType.TypeDefiniton)
                types.Add((MappedTypeDefinition)node);
            ClustersForNode(node, cluster => node.Record(RecordingStage.Parsed, cluster.Nodes));
        }

        public void MapNode(RecordingStage stage, MappedNode node)
        {
            ClustersForNode(node, cluster => node.Record(stage, cluster.Nodes));
        }

        internal void Complete(CompilerContext compileResult)
        {
            foreach (var cluster in nodeClusters)
                cluster.Resolve(
                    node =>
                        {
                            if (node.Format != null)
                                classificationSpans.Add(
                                    new ClassificationSpan(
                                        node.TextSpan.GetSnapshotSpan(bufferMap.CurrentSnapshot),
                                        service.ClassificationTypeRegistry.GetClassificationType(
                                            node.Format)));
                        }
                    );

            Errors = compileResult.Errors;
            Warnings = compileResult.Warnings;
        }

        public NodeCluster GetNodeCluster(int line, int column)
        {
            var index = IndexOfBufferPoint(line, column);
            var cluster = nodeClusters[Lookup(index)];
            if (cluster.Index + cluster.Length >= index)
                return cluster;
            return null;
        }

        public NodeCluster GetNodeCluster(SourceLocation location)
        {
            var bufferPoint = bufferMap.LocationToPoint(location);
            return GetNodeCluster(bufferPoint.Line, bufferPoint.Column);
        }

        public NodeCluster GetAdjacentNodeCluster(int line, int column)
        {
            return nodeClusters[Lookup(IndexOfBufferPoint(line, column))];
        }

        public SnapshotSpan GetErrorSnapshotSpan(LexicalInfo lexicalInfo)
        {
            var pos = bufferMap.LocationToPoint(lexicalInfo);
            var cluster = GetNodeCluster(lexicalInfo);
            if (cluster != null)
                return cluster.Nodes.LastOrDefault().TextSpan.GetSnapshotSpan(bufferMap.CurrentSnapshot);
            
            var line = bufferMap.CurrentSnapshot.GetLineFromLineNumber(pos.Line);
            var start = line.Start + pos.Column;

            return new SnapshotSpan(bufferMap.CurrentSnapshot, start, line.End - start);
        }

        public IEnumerable<MappedTypeDefinition> GetTypes()
        {
            return types;
        }

        internal MappedNode GetMappedNode(Node astNode)
        {
            var cluster = GetNodeCluster(astNode.LexicalInfo);
            if (cluster == null)
                return null;
            return cluster.Nodes.Where(n => n.Node == astNode).FirstOrDefault();
        }
    }
}
