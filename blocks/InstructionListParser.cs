﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using Mono.Cecil.Cil;

namespace de4dot.blocks {
	class InstructionListParser {
		IList<Instruction> instructions;
		IList<ExceptionHandler> exceptionHandlers;
		Dictionary<Instruction, int> instrToIndex;
		Dictionary<int, List<int>> branches;	// key = dest index, value = instrs branching to dest

		public InstructionListParser(IList<Instruction> instructions, IList<ExceptionHandler> exceptionHandlers) {
			this.instructions = instructions;
			this.exceptionHandlers = exceptionHandlers;
			this.branches = new Dictionary<int, List<int>>();

			createInstrToIndex();
			createBranches();
			createExceptionBranches();
		}

		void createInstrToIndex() {
			instrToIndex = new Dictionary<Instruction, int>();

			for (int i = 0; i < instructions.Count; i++)
				instrToIndex[instructions[i]] = i;
		}

		List<int> getBranchTargetList(int index) {
			List<int> targetsList;
			if (!branches.TryGetValue(index, out targetsList))
				branches[index] = targetsList = new List<int>();
			return targetsList;
		}

		void markAsBranchTarget(Instruction instr) {
			if (instr == null)
				return;

			int index = instrToIndex[instr];
			getBranchTargetList(index);	// Just create the list
		}

		void createExceptionBranches() {
			foreach (var eh in exceptionHandlers) {
				markAsBranchTarget(eh.TryStart);
				markAsBranchTarget(eh.TryEnd);
				markAsBranchTarget(eh.FilterStart);
				markAsBranchTarget(eh.HandlerStart);
				markAsBranchTarget(eh.HandlerEnd);
			}
		}

		void createBranches() {
			for (int i = 0; i < instructions.Count; i++) {
				var instr = instructions[i];

				List<int> targets = null;
				switch (instr.OpCode.OperandType) {
				case OperandType.ShortInlineBrTarget:
				case OperandType.InlineBrTarget:
					var targetInstr = instr.Operand as Instruction;
					if (targetInstr != null)
						targets = new List<int> { instrToIndex[targetInstr] };
					break;

				case OperandType.InlineSwitch:
					var switchTargets = (Instruction[])instr.Operand;
					targets = new List<int>(switchTargets.Length);
					for (int j = 0; j < switchTargets.Length; j++) {
						var target = switchTargets[j];
						if (target == null)
							continue;
						targets.Add(instrToIndex[target]);
					}
					break;

				default:
					switch (instr.OpCode.Code) {
					case Code.Endfilter:
					case Code.Endfinally:
					case Code.Jmp:
					case Code.Ret:
					case Code.Rethrow:
					case Code.Throw:
						targets = new List<int>();
						break;
					}
					break;
				}

				if (targets != null) {
					if (i + 1 < instructions.Count)
						targets.Add(i + 1);
					for (int j = 0; j < targets.Count; j++) {
						int targetIndex = targets[j];
						getBranchTargetList(targetIndex).Add(i);
					}
				}
			}
		}

		void findBlocks(List<Block> instrToBlock, List<Block> allBlocks) {
			Block block = null;
			for (var i = 0; i < instructions.Count; i++) {
				List<int> branchSources;
				if (branches.TryGetValue(i, out branchSources) || block == null) {
					block = new Block();
					allBlocks.Add(block);
				}

				block.add(new Instr(this.instructions[i]));
				instrToBlock.Add(block);
			}
		}

		// Fix all branches so they now point to a Block, and not an Instruction. The
		// block's Targets field is updated, not the Instruction's Operand field.
		// Also update Block.FallThrough with next Block if last instr falls through.
		void fixBranchTargets(List<Block> instrToBlock, List<Block> allBlocks) {
			for (var i = 0; i < allBlocks.Count; i++) {
				var block = allBlocks[i];
				var lastInstr = block.LastInstr;

				switch (lastInstr.OpCode.OperandType) {
				case OperandType.ShortInlineBrTarget:
				case OperandType.InlineBrTarget:
					var targetInstr = lastInstr.Operand as Instruction;
					if (targetInstr != null)
						block.Targets = new List<Block> { instrToBlock[instrToIndex[targetInstr]] };
					break;

				case OperandType.InlineSwitch:
					var switchTargets = (Instruction[])lastInstr.Operand;
					var newSwitchTargets = new List<Block>();
					block.Targets = newSwitchTargets;
					foreach (var target in switchTargets) {
						if (target != null)
							newSwitchTargets.Add(instrToBlock[instrToIndex[target]]);
					}
					break;
				}

				if (i + 1 < allBlocks.Count && Instr.isFallThrough(lastInstr.OpCode))
					block.FallThrough = allBlocks[i + 1];
			}
		}

		// Updates the sources field of each block
		void fixBlockSources(List<Block> allBlocks) {
			foreach (var block in allBlocks) {
				block.updateSources();
			}
		}

		class EHInfo {
			public ExceptionHandler eh;

			public EHInfo(ExceptionHandler eh) {
				this.eh = eh;
			}

			public override int GetHashCode() {
				int res = eh.TryStart.GetHashCode();
				if (eh.TryEnd != null)
					res += eh.TryEnd.GetHashCode();
				return res;
			}

			public override bool Equals(object obj) {
				var other = obj as EHInfo;
				if (other == null)
					return false;
				return ReferenceEquals(eh.TryStart, other.eh.TryStart) &&
					   ReferenceEquals(eh.TryEnd, other.eh.TryEnd);
			}
		}

		List<List<ExceptionHandler>> getSortedExceptionInfos() {
			var exInfos = new Dictionary<EHInfo, List<ExceptionHandler>>();
			foreach (var eh in exceptionHandlers) {
				List<ExceptionHandler> handlers;
				if (!exInfos.TryGetValue(new EHInfo(eh), out handlers))
					exInfos[new EHInfo(eh)] = handlers = new List<ExceptionHandler>();

				handlers.Add(eh);
				if (!ReferenceEquals(handlers[0].TryEnd, eh.TryEnd))
					throw new ApplicationException("Exception handler's try block does not start and end at the same place as the other one.");
			}

			var exSorted = new List<List<ExceptionHandler>>(exInfos.Values);
			exSorted.Sort((a, b) => {
				int ai, bi;

				// Sort in reverse order of TryStart. This is to make sure that nested
				// try handlers are before the outer try handler.
				ai = instrToIndex[a[0].TryStart];
				bi = instrToIndex[b[0].TryStart];
				if (ai > bi) return -1;
				if (ai < bi) return 1;

				// Same start instruction. The nested one is the one that ends earliest,
				// so it should be sorted before the outer one.
				ai = getInstrIndex(a[0].TryEnd);
				bi = getInstrIndex(b[0].TryEnd);
				if (ai < bi) return -1;
				if (ai > bi) return 1;

				return 0;
			});

			return exSorted;
		}

		class BaseBlocksList {
			class BaseBlockInfo {
				public int startInstr, endInstr;
				public BaseBlock baseBlock;

				public BaseBlockInfo(int start, int end, BaseBlock bb) {
					startInstr = start;
					endInstr = end;
					baseBlock = bb;
				}
			}

			List<BaseBlockInfo> blocksLeft = new List<BaseBlockInfo>();

			public void add(BaseBlock bb, int start, int end) {
				if (start < 0 || end < 0 || end < start)
					throw new ApplicationException("Invalid start and/or end index");
				if (blocksLeft.Count != 0) {
					var bbi = blocksLeft[blocksLeft.Count - 1];
					if (bbi.endInstr + 1 != start)
						throw new ApplicationException("Previous BaseBlock does not end where this new one starts");
				}
				blocksLeft.Add(new BaseBlockInfo(start, end, bb));
			}

			int findStart(int instrIndex) {
				for (int i = 0; i < blocksLeft.Count; i++) {
					if (blocksLeft[i].startInstr == instrIndex)
						return i;
				}
				throw new ApplicationException("Could not find start BaseBlockInfo");
			}

			int findEnd(int instrIndex) {
				for (int i = 0; i < blocksLeft.Count; i++) {
					if (blocksLeft[i].endInstr == instrIndex)
						return i;
				}
				throw new ApplicationException("Could not find end BaseBlockInfo");
			}

			List<BaseBlock> getBlocks(int startInstr, int endInstr, out int startIndex, out int endIndex) {
				if (endInstr < startInstr || startInstr < 0 || endInstr < 0)
					throw new ApplicationException("Invalid startInstr and/or endInstr");

				var rv = new List<BaseBlock>();

				startIndex = findStart(startInstr);
				endIndex = findEnd(endInstr);

				for (int i = startIndex; i <= endIndex; i++)
					rv.Add(blocksLeft[i].baseBlock);

				return rv;
			}

			// Replace the BaseBlocks with a new BaseBlock, returning the old ones.
			public List<BaseBlock> replace(int startInstr, int endInstr, ScopeBlock bb) {
				if (endInstr < startInstr)
					return new List<BaseBlock>();

				int startIndex, endIndex;
				var rv = getBlocks(startInstr, endInstr, out startIndex, out endIndex);
				updateParent(rv, bb);

				var bbi = new BaseBlockInfo(blocksLeft[startIndex].startInstr, blocksLeft[endIndex].endInstr, bb);
				blocksLeft.RemoveRange(startIndex, endIndex - startIndex + 1);
				blocksLeft.Insert(startIndex, bbi);

				return rv;
			}

			public List<BaseBlock> getBlocks(ScopeBlock parent) {
				if (blocksLeft.Count == 0)
					return new List<BaseBlock>();
				int startIndex, endIndex;
				var lb = getBlocks(0, blocksLeft[blocksLeft.Count - 1].endInstr, out startIndex, out endIndex);
				return updateParent(lb, parent);
			}

			List<BaseBlock> updateParent(List<BaseBlock> lb, ScopeBlock parent) {
				foreach (var bb in lb)
					bb.Parent = parent;
				return lb;
			}
		}

		BaseBlocksList createBaseBlockList(List<Block> allBlocks, List<List<ExceptionHandler>> exSorted) {
			var bbl = new BaseBlocksList();
			foreach (var block in allBlocks) {
				int start = instrToIndex[block.FirstInstr.Instruction];
				int end = instrToIndex[block.LastInstr.Instruction];
				bbl.add(block, start, end);
			}

			foreach (var exHandlers in exSorted) {
				var tryBlock = new TryBlock();
				var tryStart = instrToIndex[exHandlers[0].TryStart];
				var tryEnd = getInstrIndex(exHandlers[0].TryEnd) - 1;
				tryBlock.BaseBlocks = bbl.replace(tryStart, tryEnd, tryBlock);

				foreach (var exHandler in exHandlers) {
					var tryHandlerBlock = new TryHandlerBlock(exHandler);
					tryBlock.addTryHandler(tryHandlerBlock);

					int filterStart = -1, handlerStart = -1, handlerEnd = -1;

					if (exHandler.FilterStart != null) {
						filterStart = instrToIndex[exHandler.FilterStart];
						var end = instrToIndex[exHandler.HandlerStart] - 1;
						tryHandlerBlock.FilterHandlerBlock.BaseBlocks = bbl.replace(filterStart, end, tryHandlerBlock.FilterHandlerBlock);
					}

					handlerStart = instrToIndex[exHandler.HandlerStart];
					handlerEnd = getInstrIndex(exHandler.HandlerEnd) - 1;
					tryHandlerBlock.HandlerBlock.BaseBlocks = bbl.replace(handlerStart, handlerEnd, tryHandlerBlock.HandlerBlock);

					tryHandlerBlock.BaseBlocks = bbl.replace(filterStart == -1 ? handlerStart : filterStart, handlerEnd, tryHandlerBlock);
				}
			}

			return bbl;
		}

		int getInstrIndex(Instruction instruction) {
			if (instruction == null)
				return instructions.Count;
			return instrToIndex[instruction];
		}

		public MethodBlocks parse() {
			var instrToBlock = new List<Block>(instructions.Count);
			var allBlocks = new List<Block>();
			findBlocks(instrToBlock, allBlocks);
			fixBranchTargets(instrToBlock, allBlocks);
			fixBlockSources(allBlocks);
			var exSorted = getSortedExceptionInfos();
			var bbl = createBaseBlockList(allBlocks, exSorted);

			foreach (var block in allBlocks)
				block.removeLastBr();

			var mb = new MethodBlocks();
			mb.BaseBlocks = bbl.getBlocks(mb);
			return mb;
		}
	}
}
