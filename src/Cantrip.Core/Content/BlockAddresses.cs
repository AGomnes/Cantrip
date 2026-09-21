using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Syntax;

namespace Cantrip.Content
{
    /// <summary>
    /// Stable names for every block of loaded content, such as <c>card:Prepare/effect/0.body</c>.
    /// Snapshots store scheduled work by address instead of by object reference, so a save file
    /// stays valid across process restarts and across hot reloads that keep the block in place.
    /// </summary>
    internal sealed class BlockAddressBook
    {
        private readonly Dictionary<BlockNode, string> _addresses = new Dictionary<BlockNode, string>();
        private readonly Dictionary<string, BlockNode> _blocks = new Dictionary<string, BlockNode>(StringComparer.Ordinal);

        public static BlockAddressBook Build(ContentLibrary content)
        {
            var book = new BlockAddressBook();

            foreach (EntityDefinition definition in content.Definitions)
            {
                string root = definition.KindName + ":" + definition.Name;
                int listener = 0;

                foreach (MemberNode member in definition.Syntax.Members)
                {
                    switch (member)
                    {
                        case BlockMemberNode block when block.Name == "move":
                            string move = block.Arguments.Count > 0 ? EntityDefinition.ReadWords(block.Arguments[0]).FirstOrDefault() ?? "?" : "?";
                            book.Add(root + "/move:" + move, block.Body);
                            break;
                        case BlockMemberNode block:
                            book.Add(root + "/" + block.Name, block.Body);
                            break;
                        case ListenerNode on:
                            book.Add(root + "/on#" + listener++, on.Body);
                            break;
                    }
                }
            }

            foreach (VerbDefinition verb in content.Verbs) book.Add("verb:" + verb.Name, verb.Body);
            return book;
        }

        public string? AddressOf(BlockNode block) => _addresses.TryGetValue(block, out string? address) ? address : null;

        public BlockNode? Resolve(string address) => _blocks.TryGetValue(address, out BlockNode? block) ? block : null;

        private void Add(string address, BlockNode block)
        {
            if (_blocks.ContainsKey(address)) return;
            _blocks[address] = block;
            _addresses[block] = address;

            for (int i = 0; i < block.Statements.Count; i++)
            {
                string at = address + "/" + i;
                switch (block.Statements[i])
                {
                    case IfNode branch:
                        Add(at + ".then", branch.Then);
                        if (branch.Else != null) Add(at + ".else", branch.Else);
                        break;
                    case ChanceNode chance:
                        Add(at + ".body", chance.Body);
                        if (chance.Else != null) Add(at + ".else", chance.Else);
                        break;
                    case RepeatNode repeat:
                        Add(at + ".body", repeat.Body);
                        break;
                    case ForEachNode loop:
                        Add(at + ".body", loop.Body);
                        break;
                    case ScheduleNode schedule:
                        Add(at + ".body", schedule.Body);
                        break;
                    case LabeledBlockNode labeled:
                        Add(at + ".body", labeled.Body);
                        break;
                }
            }
        }
    }
}
