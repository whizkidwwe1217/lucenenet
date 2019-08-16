using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Console = Lucene.Net.Support.SystemConsole;

namespace Lucene.Net.Analysis
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    using AttributeSource = Lucene.Net.Util.AttributeSource;

    //using RollingBuffer = Lucene.Net.Util.RollingBuffer;

    // TODO: cut SynFilter over to this
    // TODO: somehow add "nuke this input token" capability...

    
    public class LookaheadTokenFilter
    {
        /// <summary>
        /// Holds all state for a single position; subclass this
        /// to record other state at each position.
        /// </summary>
        public class Position : Lucene.Net.Util.RollingBuffer.IResettable // LUCENENET TODO: API - De-nest and rename LookaheadTokenFilterPosition
        {
            // Buffered input tokens at this position:
            public readonly IList<AttributeSource.State> InputTokens = new List<AttributeSource.State>();

            // Next buffered token to be returned to consumer:
            public int NextRead;

            // Any token leaving from this position should have this startOffset:
            public int StartOffset = -1;

            // Any token arriving to this position should have this endOffset:
            public int EndOffset = -1;

            public void Reset()
            {
                InputTokens.Clear();
                NextRead = 0;
                StartOffset = -1;
                EndOffset = -1;
            }

            public virtual void Add(AttributeSource.State state)
            {
                InputTokens.Add(state);
            }

            public virtual AttributeSource.State NextState()
            {
                Debug.Assert(NextRead < InputTokens.Count);
                return InputTokens[NextRead++];
            }
        }
    }

    /// <summary>
    /// An abstract <see cref="TokenFilter"/> to make it easier to build graph
    /// token filters requiring some lookahead.  This class handles
    /// the details of buffering up tokens, recording them by
    /// position, restoring them, providing access to them, etc.
    /// </summary>
    public abstract class LookaheadTokenFilter<T> : TokenFilter
        where T : LookaheadTokenFilter.Position
    {
        protected readonly static bool DEBUG = 
#if VERBOSE_TEST_LOGGING
            true
#else
            false
#endif
            ;

        protected internal readonly IPositionIncrementAttribute m_posIncAtt;// = AddAttribute<PositionIncrementAttribute>();
        protected internal readonly IPositionLengthAttribute m_posLenAtt;// = addAttribute(typeof(PositionLengthAttribute));
        protected internal readonly IOffsetAttribute m_offsetAtt;// = addAttribute(typeof(OffsetAttribute));

        // Position of last read input token:
        protected internal int m_inputPos;

        // Position of next possible output token to return:
        protected internal int m_outputPos;

        // True if we hit end from our input:
        protected internal bool m_end;

        private bool tokenPending;
        private bool insertPending;

        protected internal LookaheadTokenFilter(TokenStream input)
            : base(input)
        {
            positions = new RollingBufferAnonymousInnerClassHelper(this);
            m_posIncAtt = AddAttribute<IPositionIncrementAttribute>();
            m_posLenAtt = AddAttribute<IPositionLengthAttribute>();
            m_offsetAtt = AddAttribute<IOffsetAttribute>();
        }

        /// <summary>
        /// Call this only from within <see cref="AfterPosition()"/>, to insert a new
        /// token. After calling this you should set any
        /// necessary token you need.
        /// </summary>
        protected internal virtual void InsertToken()
        {
            if (tokenPending)
            {
                positions.Get(m_inputPos).Add(CaptureState());
                tokenPending = false;
            }
            Debug.Assert(!insertPending);
            insertPending = true;
        }

        /// <summary>
        /// This is called when all input tokens leaving a given
        /// position have been returned.  Override this and
        /// call insertToken and then set whichever token's
        /// attributes you want, if you want to inject
        /// a token starting from this position.
        /// </summary>
        protected internal virtual void AfterPosition()
        {
        }

        protected internal abstract T NewPosition();

        protected internal RollingBuffer<LookaheadTokenFilter.Position> positions;

        private class RollingBufferAnonymousInnerClassHelper : RollingBuffer<LookaheadTokenFilter.Position>
        {
            private LookaheadTokenFilter<T> outerInstance;

            public RollingBufferAnonymousInnerClassHelper(LookaheadTokenFilter<T> outerInstance)
                : base(outerInstance.NewPosition)
            {
                this.outerInstance = outerInstance;
            }

            protected override LookaheadTokenFilter.Position NewInstance()
            {
                return outerInstance.NewPosition();
            }
        }

        /// <summary>
        /// Returns true if there is a new token. </summary>
        protected internal virtual bool PeekToken()
        {
            if (DEBUG)
            {
                Console.WriteLine("LTF.peekToken inputPos=" + m_inputPos + " outputPos=" + m_outputPos + " tokenPending=" + tokenPending);
            }
            Debug.Assert(!m_end);
            Debug.Assert(m_inputPos == -1 || m_outputPos <= m_inputPos);
            if (tokenPending)
            {
                positions.Get(m_inputPos).Add(CaptureState());
                tokenPending = false;
            }
            bool gotToken = m_input.IncrementToken();
            if (DEBUG)
            {
                Console.WriteLine("  input.incrToken() returned " + gotToken);
            }
            if (gotToken)
            {
                m_inputPos += m_posIncAtt.PositionIncrement;
                Debug.Assert(m_inputPos >= 0);
                if (DEBUG)
                {
                    Console.WriteLine("  now inputPos=" + m_inputPos);
                }

                LookaheadTokenFilter.Position startPosData = positions.Get(m_inputPos);
                LookaheadTokenFilter.Position endPosData = positions.Get(m_inputPos + m_posLenAtt.PositionLength);

                int startOffset = m_offsetAtt.StartOffset;
                if (startPosData.StartOffset == -1)
                {
                    startPosData.StartOffset = startOffset;
                }
                else
                {
                    // Make sure our input isn't messing up offsets:
                    Debug.Assert(startPosData.StartOffset == startOffset, "prev startOffset=" + startPosData.StartOffset + " vs new startOffset=" + startOffset + " inputPos=" + m_inputPos);
                }

                int endOffset = m_offsetAtt.EndOffset;
                if (endPosData.EndOffset == -1)
                {
                    endPosData.EndOffset = endOffset;
                }
                else
                {
                    // Make sure our input isn't messing up offsets:
                    Debug.Assert(endPosData.EndOffset == endOffset, "prev endOffset=" + endPosData.EndOffset + " vs new endOffset=" + endOffset + " inputPos=" + m_inputPos);
                }

                tokenPending = true;
            }
            else
            {
                m_end = true;
            }

            return gotToken;
        }

        /// <summary>
        /// Call this when you are done looking ahead; it will set
        /// the next token to return.  Return the boolean back to
        /// the caller.
        /// </summary>
        protected internal virtual bool NextToken()
        {
            //System.out.println("  nextToken: tokenPending=" + tokenPending);
            if (DEBUG)
            {
                Console.WriteLine("LTF.nextToken inputPos=" + m_inputPos + " outputPos=" + m_outputPos + " tokenPending=" + tokenPending);
            }

            LookaheadTokenFilter.Position posData = positions.Get(m_outputPos);

            // While loop here in case we have to
            // skip over a hole from the input:
            while (true)
            {
                //System.out.println("    check buffer @ outputPos=" +
                //outputPos + " inputPos=" + inputPos + " nextRead=" +
                //posData.nextRead + " vs size=" +
                //posData.inputTokens.size());

                // See if we have a previously buffered token to
                // return at the current position:
                if (posData.NextRead < posData.InputTokens.Count)
                {
                    if (DEBUG)
                    {
                        Console.WriteLine("  return previously buffered token");
                    }
                    // this position has buffered tokens to serve up:
                    if (tokenPending)
                    {
                        positions.Get(m_inputPos).Add(CaptureState());
                        tokenPending = false;
                    }
                    RestoreState(positions.Get(m_outputPos).NextState());
                    //System.out.println("      return!");
                    return true;
                }

                if (m_inputPos == -1 || m_outputPos == m_inputPos)
                {
                    // No more buffered tokens:
                    // We may still get input tokens at this position
                    //System.out.println("    break buffer");
                    if (tokenPending)
                    {
                        // Fast path: just return token we had just incr'd,
                        // without having captured/restored its state:
                        if (DEBUG)
                        {
                            Console.WriteLine("  pass-through: return pending token");
                        }
                        tokenPending = false;
                        return true;
                    }
                    else if (m_end || !PeekToken())
                    {
                        if (DEBUG)
                        {
                            Console.WriteLine("  END");
                        }
                        AfterPosition();
                        if (insertPending)
                        {
                            // Subclass inserted a token at this same
                            // position:
                            if (DEBUG)
                            {
                                Console.WriteLine("  return inserted token");
                            }
                            Debug.Assert(InsertedTokenConsistent());
                            insertPending = false;
                            return true;
                        }

                        return false;
                    }
                }
                else
                {
                    if (posData.StartOffset != -1)
                    {
                        // this position had at least one token leaving
                        if (DEBUG)
                        {
                            Console.WriteLine("  call afterPosition");
                        }
                        AfterPosition();
                        if (insertPending)
                        {
                            // Subclass inserted a token at this same
                            // position:
                            if (DEBUG)
                            {
                                Console.WriteLine("  return inserted token");
                            }
                            Debug.Assert(InsertedTokenConsistent());
                            insertPending = false;
                            return true;
                        }
                    }

                    // Done with this position; move on:
                    m_outputPos++;
                    if (DEBUG)
                    {
                        Console.WriteLine("  next position: outputPos=" + m_outputPos);
                    }
                    positions.FreeBefore(m_outputPos);
                    posData = positions.Get(m_outputPos);
                }
            }
        }

        // If subclass inserted a token, make sure it had in fact
        // looked ahead enough:
        private bool InsertedTokenConsistent()
        {
            int posLen = m_posLenAtt.PositionLength;
            LookaheadTokenFilter.Position endPosData = positions.Get(m_outputPos + posLen);
            Debug.Assert(endPosData.EndOffset != -1);
            Debug.Assert(m_offsetAtt.EndOffset == endPosData.EndOffset, "offsetAtt.endOffset=" + m_offsetAtt.EndOffset + " vs expected=" + endPosData.EndOffset);
            return true;
        }

        // TODO: end()?
        // TODO: close()?

        public override void Reset()
        {
            base.Reset();
            positions.Reset();
            m_inputPos = -1;
            m_outputPos = 0;
            tokenPending = false;
            m_end = false;
        }
    }
}