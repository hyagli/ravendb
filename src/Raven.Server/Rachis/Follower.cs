﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Raven.Server.ServerWide.Context;
using Sparrow;
using Voron;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.Tables;

namespace Raven.Server.Rachis
{
    public class Follower : IDisposable
    {
        private readonly RachisConsensus _engine;
        private readonly RemoteConnection _connection;
        private Thread _thread;

        public Follower(RachisConsensus engine, RemoteConnection remoteConnection)
        {
            _engine = engine;
            _connection = remoteConnection;
        }

        private void FollowerSteadyState()
        {
            var entries = new List<RachisEntry>();
            long lastCommit = 0, lastTruncate = 0;
            while (true)
            {
                entries.Clear();

                TransactionOperationContext context;
                using (_engine.ContextPool.AllocateOperationContext(out context))
                {
                    var appendEntries = _connection.Read<AppendEntries>(context);
                    _engine.Timeout.Defer();
                    if (appendEntries.EntriesCount != 0)
                    {
                        for (int i = 0; i < appendEntries.EntriesCount; i++)
                        {
                            entries.Add(_connection.ReadRachisEntry(context));
                            _engine.Timeout.Defer();
                        }
                    }

                    long lastLogIndex = appendEntries.PrevLogIndex;

                    // don't start write transaction fro noop
                    if (lastCommit != appendEntries.LeaderCommit ||
                        lastTruncate != appendEntries.TruncateLogBefore ||
                        entries.Count != 0)
                    {
                        // we start the tx after we finished reading from the network
                        using (var tx = context.OpenWriteTransaction())
                        {
                            if (entries.Count > 0)
                            {
                                using (var lastTopology = _engine.AppendToLog(context, entries))
                                {
                                    if (lastTopology != null)
                                    {
                                        RachisConsensus.SetTopology(_engine, context.Transaction.InnerTransaction,
                                            lastTopology);
                                    }
                                }
                            }

                            lastLogIndex = _engine.GetLastEntryIndex(context);

                            var lastEntryIndexToCommit = Math.Min(
                                lastLogIndex,
                                appendEntries.LeaderCommit);


                            var lastAppliedIndex = _engine.GetLastCommitIndex(context);

                            if (lastEntryIndexToCommit > lastAppliedIndex)
                            {
                                _engine.Apply(context, lastEntryIndexToCommit);
                            }

                            lastTruncate = Math.Min(appendEntries.TruncateLogBefore, lastEntryIndexToCommit);
                            _engine.TruncateLogBefore(context, lastTruncate);
                            lastCommit = lastEntryIndexToCommit;

                            tx.Commit();
                        }
                    }

                    _connection.Send(context, new AppendEntriesResponse
                    {
                        CurrentTerm = _engine.CurrentTerm,
                        LastLogIndex = lastLogIndex,
                        Success = true
                    });

                    _engine.Timeout.Defer();

                }
            }
        }

        private LogLengthNegotiation CheckIfValidLeader()
        {
            TransactionOperationContext context;
            using (_engine.ContextPool.AllocateOperationContext(out context))
            {
                var logLength = _connection.Read<LogLengthNegotiation>(context);

                if (logLength.Term < _engine.CurrentTerm)
                {
                    _connection.Send(context, new LogLengthNegotiationResponse
                    {
                        Status = LogLengthNegotiationResponse.ResponseStatus.Rejected,
                        Message = $"The incoming term {logLength.Term} is smaller than current term {_engine.CurrentTerm} and is therefor rejected",
                        CurrentTerm = _engine.CurrentTerm
                    });
                    _connection.Dispose();
                    return null;
                }

                _engine.Timeout.Defer();
                return logLength;
            }
        }

        private void NegotiateWithLeader(TransactionOperationContext context, LogLengthNegotiation negotiation)
        {
            // only the leader can send append entries, so if we accepted it, it's the leader
            
            if (negotiation.Term > _engine.CurrentTerm)
            {
                _engine.FoundAboutHigherTerm(negotiation.Term);
            }

            long prevTerm;
            using (context.OpenReadTransaction())
            {
                prevTerm = _engine.GetTermFor(context, negotiation.PrevLogIndex) ?? 0;
            }
            if (prevTerm != negotiation.PrevLogTerm)
            {
                // we now have a mismatch with the log position, and need to negotiate it with 
                // the leader
                NegotiateMatchEntryWithLeaderAndApplyEntries(context, _connection, negotiation);
            }
            else
            {
                // this (or the negotiation above) completes the negotiation process
                _connection.Send(context, new LogLengthNegotiationResponse
                {
                    Status = LogLengthNegotiationResponse.ResponseStatus.Acceptable,
                    Message = $"Found a log index / term match at {negotiation.PrevLogIndex} with term {prevTerm}",
                    CurrentTerm = _engine.CurrentTerm,
                    LastLogIndex = negotiation.PrevLogIndex
                });
            }

            _engine.Timeout.Defer();

            // at this point, the leader will send us a snapshot message
            // in most cases, it is an empty snapshot, then start regular append entries
            // the reason we send this is to simplify the # of states in the protocol

            var snapshot = _connection.ReadInstallSnapshot(context);

            using (context.OpenWriteTransaction())
            {
                if (InstallSnapshot(context))
                {
                    _engine.SetLastCommitIndex(context, snapshot.LastIncludedIndex, snapshot.LastIncludedTerm);
                    _engine.TruncateLogBefore(context, snapshot.LastIncludedIndex);
                }
                else
                {
                    var lastEntryIndex = _engine.GetLastEntryIndex(context);
                    if (lastEntryIndex < snapshot.LastIncludedIndex)
                    {
                        throw new InvalidOperationException($"The snapshot installation had failed because the last included index {snapshot.LastIncludedIndex} in term {snapshot.LastIncludedTerm} doesn't match the last entry {lastEntryIndex}");
                    }
                }

                // snapshot always has the latest topology
                if (snapshot.Topology == null)
                    throw new InvalidOperationException("Expected to get topology on snapshot");
                RachisConsensus.SetTopology(_engine, context.Transaction.InnerTransaction, snapshot.Topology);

                context.Transaction.Commit();
            }
            _connection.Send(context, new InstallSnapshotResponse
            {
                Done = true,
                CurrentTerm = _engine.CurrentTerm,
                LastLogIndex = snapshot.LastIncludedIndex
            });

            using (context.OpenReadTransaction())
            {
                // notify the state machine
                _engine.SnapshotInstalled(context);
            }

            _engine.Timeout.Defer();
        }

        private unsafe bool InstallSnapshot(TransactionOperationContext context)
        {
            var txw = context.Transaction.InnerTransaction;
            var sp = Stopwatch.StartNew();
            var reader = _connection.CreateReader();
            while (true)
            {
                var type = reader.ReadInt32();
                if (type == -1)
                    return false;

                int size;
                Slice key;
                long entries;
                switch ((RootObjectType)type)
                {
                    case RootObjectType.None:
                        return true;
                    case RootObjectType.VariableSizeTree:

                        size = reader.ReadInt32();
                        reader.ReadExactly(size);
                        Tree tree;

                        using (Slice.From(context.Allocator, reader.Buffer, 0, size, ByteStringType.Immutable, out key))
                        {
                            txw.DeleteTree(key);
                            tree = txw.CreateTree(key);
                        }

                        entries = reader.ReadInt64();
                        for (long i = 0; i < entries; i++)
                        {
                            MaybeNotifyLeaderThatWeAreSillAlive(context, i, sp);

                            size = reader.ReadInt32();
                            reader.ReadExactly(size);
                            using (Slice.From(context.Allocator, reader.Buffer, 0, size, ByteStringType.Immutable, out key))
                            {
                                size = reader.ReadInt32();
                                reader.ReadExactly(size);

                                byte* ptr;
                                using (tree.DirectAdd(key, size, out ptr))
                                {
                                    fixed (byte* pBuffer = reader.Buffer)
                                    {
                                        Memory.Copy(ptr, pBuffer, size);
                                    }
                                }
                            }
                        }


                        break;
                    case RootObjectType.Table:

                        size = reader.ReadInt32();
                        reader.ReadExactly(size);
                        Table table;
                        using (Slice.From(context.Allocator, reader.Buffer, 0, size, ByteStringType.Immutable, out key))
                        {
                            var tableTree = txw.ReadTree(key, RootObjectType.Table);

                            // Get the table schema
                            var schemaSize = tableTree.GetDataSize(TableSchema.SchemasSlice);
                            var schemaPtr = tableTree.DirectRead(TableSchema.SchemasSlice);
                            if (schemaPtr == null)
                                throw new InvalidOperationException(
                                    "When trying to install snapshot, found missing table " + key);

                            var schema = TableSchema.ReadFrom(txw.Allocator, schemaPtr, schemaSize);

                            table = txw.OpenTable(schema, key);
                        }

                        // delete the table
                        TableValueReader tvr;
                        while (true)
                        {
                            if (table.SeekOnePrimaryKey(Slices.AfterAllKeys, out tvr) == false)
                                break;
                            table.Delete(tvr.Id);

                            MaybeNotifyLeaderThatWeAreSillAlive(context, table.NumberOfEntries, sp);
                        }

                        entries = reader.ReadInt64();
                        for (long i = 0; i < entries; i++)
                        {
                            MaybeNotifyLeaderThatWeAreSillAlive(context, i, sp);

                            size = reader.ReadInt32();
                            reader.ReadExactly(size);
                            fixed (byte* pBuffer = reader.Buffer)
                            {
                                tvr = new TableValueReader(pBuffer, size);
                                table.Insert(ref tvr);
                            }
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(type), type.ToString());
                }
            }
        }

        private void MaybeNotifyLeaderThatWeAreSillAlive(TransactionOperationContext context, long count, Stopwatch sp)
        {
            if (count % 100 != 0)
                return;

            if (sp.ElapsedMilliseconds <= _engine.ElectionTimeoutMs / 2)
                return;

            sp.Restart();

            _connection.Send(context, new InstallSnapshotResponse
            {
                Done = false,
                CurrentTerm = -1,
                LastLogIndex = -1
            });
        }

        private void NegotiateMatchEntryWithLeaderAndApplyEntries(TransactionOperationContext context,
            RemoteConnection connection, LogLengthNegotiation negotiation)
        {
            long minIndex;
            long maxIndex;
            long midpointTerm;
            long midpointIndex;
            using (context.OpenReadTransaction())
            {
                minIndex = _engine.GetFirstEntryIndex(context);

                if (minIndex == 0) // no entries at all
                {
                    connection.Send(context, new LogLengthNegotiationResponse
                    {
                        Status = LogLengthNegotiationResponse.ResponseStatus.Acceptable,
                        Message = "No entries at all here, give me everything from the start",
                        CurrentTerm = _engine.CurrentTerm,
                        LastLogIndex = 0
                    });

                    return; // leader will know where to start from here
                }

                maxIndex = Math.Min(
                    _engine.GetLastEntryIndex(context), // max
                    negotiation.PrevLogIndex
                );

                midpointIndex = (maxIndex + minIndex) / 2;

                midpointTerm = _engine.GetTermForKnownExisting(context, midpointIndex);
            }


            while (minIndex < maxIndex)
            {
                _engine.Timeout.Defer();

                // TODO: cancellation
                //_cancellationTokenSource.Token.ThrowIfCancellationRequested();

                connection.Send(context, new LogLengthNegotiationResponse
                {
                    Status = LogLengthNegotiationResponse.ResponseStatus.Negotiation,
                    Message =
                        $"Term/Index mismatch from leader, need to figure out at what point the logs match, range: {maxIndex} - {minIndex} | {midpointIndex} in term {midpointTerm}",
                    CurrentTerm = _engine.CurrentTerm,
                    MaxIndex = maxIndex,
                    MinIndex = minIndex,
                    MidpointIndex = midpointIndex,
                    MidpointTerm = midpointTerm
                });

                var response = connection.Read<LogLengthNegotiation>(context);

                _engine.Timeout.Defer();

                using (context.OpenReadTransaction())
                {
                    if (_engine.GetTermFor(context, response.PrevLogIndex) == response.PrevLogTerm)
                    {
                        minIndex = midpointIndex + 1;
                    }
                    else
                    {
                        maxIndex = midpointIndex - 1;
                    }
                }
                midpointIndex = (maxIndex + minIndex) / 2;
                using (context.OpenReadTransaction())
                    midpointTerm = _engine.GetTermForKnownExisting(context, midpointIndex);
            }

            connection.Send(context, new LogLengthNegotiationResponse()
            {
                Status = LogLengthNegotiationResponse.ResponseStatus.Acceptable,
                Message = $"Found a log index / term match at {midpointIndex} with term {midpointTerm}",
                CurrentTerm = _engine.CurrentTerm,
                LastLogIndex = midpointIndex
            });
        }

        public void TryAcceptConnection()
        {
            var negotiation = CheckIfValidLeader();
            if (negotiation == null)
            {
                _connection.Dispose();
                return; // did not accept connection
            }

            // if leader / candidate, this remove them from play and revert to follower mode
            _engine.SetNewState(RachisConsensus.State.Follower, this);
            _engine.Timeout.Start(_engine.SwitchToCandidateState);

            _thread = new Thread(Run)
            {
                Name = $"Follower thread from {_connection} in term {negotiation.Term}",
                IsBackground = true
            };
            _thread.Start(negotiation);
        }

        private void Run(object obj)
        {
            try
            {
                using (this)
                {
                    try
                    {
                        TransactionOperationContext context;
                        using (_engine.ContextPool.AllocateOperationContext(out context))
                        {
                            NegotiateWithLeader(context, (LogLengthNegotiation)obj);
                        }

                        FollowerSteadyState();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (AggregateException ae)
                        when (
                            ae.InnerException is OperationCanceledException ||
                            ae.InnerException is ObjectDisposedException)
                    {
                    }
                    catch (Exception e)
                    {
                        TransactionOperationContext context;
                        using (_engine.ContextPool.AllocateOperationContext(out context))
                        {
                            _connection.Send(context, e);
                        }
                        if (_engine.Log.IsInfoEnabled)
                        {
                            _engine.Log.Info("Failed to talk to leader: " + _engine.Url, e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_engine.Log.IsInfoEnabled)
                {
                    _engine.Log.Info("Failed to dispose follower when talking leader: " + _engine.Url, e);
                }
            }
        }

        public void Dispose()
        {
            _connection.Dispose();

            if (_thread != null &&
                _thread.ManagedThreadId != Thread.CurrentThread.ManagedThreadId)
                _thread.Join();
        }
    }
}