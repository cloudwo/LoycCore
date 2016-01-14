﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Loyc.Collections;

namespace Loyc.Syntax.Lexing
{
	/// <summary>The recommended base class for lexers generated by LLLPG,
	/// when not using the <c>inputSource</c> option.</summary>
	/// <remarks>
	/// If you are using the <c>inputSource</c> and <c>inputClass</c> options of,
	/// LLLPG, use <see cref="LexerSource{CharSource}"/> instead. If you want to
	/// write a lexer that implements <see cref="ILexer{Tok}"/> (so it is compatible
	/// with postprocessors like <see cref="IndentTokenGenerator"/> and 
	/// <see cref="TokensToTree"/>), use <see cref="BaseILexer{CharSrc,Tok}"/> as 
	/// your base class instead.
	/// <para/>
	/// This class contains many methods required by LLLPG, such as 
	/// <see cref="NewSet"/>, <see cref="LA(int)"/>, <see cref="LA0"/>, 
	/// <see cref="Skip"/>, <see cref="Match"/>(...), and <see 
	/// cref="TryMatch"/>(...), along with a few properties that are not 
	/// used by LLLPG that you still might want to have around, such as 
	/// <see cref="FileName"/>, <see cref="CharSource"/> and 
	/// <see cref="SourceFile"/>.
	/// <para/>
	/// It also implements the caching behavior for which <see cref="ICharSource"/>
	/// was created. See the documentation of <see cref="ICharSource"/> for more
	/// information.
	/// <para/>
	/// All lexers derived from BaseLexer should call <see cref="AfterNewline()"/>
	/// at the end of their newline rule, in order to increment the current line
	/// number. Alternately, your lexer can borrow the newline parser built into 
	/// BaseLexer, which is called <see cref="Newline()"/> and calls 
	/// <see cref="AfterNewline()"/> for you. It is possible to have LLLPG treat 
	/// this method as a rule, and tell LLLPG the meaning of the rule like this:
	/// <code>
	///	  extern token Newline @[ '\r' '\n'? | '\n' ];
	///	  // BaseLexer also defines a Spaces() method, which behaves like this:
	///	  extern token Spaces  @[ (' '|'\t')* ]; 
	/// </code>
	/// The <c>extern</c> modifier tells LLLPG not to generate code for the
	/// rule, but the rule must still have a body so that LLLPG can perform 
	/// prediction.
	/// <para/>
	/// By default, errors are handled by throwing <see cref="FormatException"/>.
	/// The recommended way to alter this behavior is to change the 
	/// <see cref="ErrorSink"/> property. For example, set it to 
	/// <see cref="MessageSink.Console"/> to send errors to the console, or
	/// use <see cref="MessageSink.FromDelegate"/> to provide a custom handler.
	/// </remarks>
	/// <typeparam name="CharSrc">A class that implements <see cref="ICharSource"/>.
	/// In order to write lexers that can accept any source of characters, set 
	/// CharSrc=ICharSource. For maximum performance when parsing strings (or
	/// to avoid memory allocation), set CharSrc=UString (<see cref="UString"/> 
	/// is a wrapper around <c>System.String</c> that, among other things, 
	/// implements <c>ICharSource</c>; please note that C# will implicitly convert 
	/// normal strings to <see cref="UString"/> for you).</typeparam>
	public abstract class BaseLexer<CharSrc> : IIndexToLine
		where CharSrc : ICharSource
	{
		protected static HashSet<int> NewSet(params int[] items) { return new HashSet<int>(items); }
		protected static HashSet<int> NewSetOfRanges(params int[] ranges) 
		{
			var set = new HashSet<int>();
			for (int r = 0; r < ranges.Length; r += 2)
				for (int i = ranges[r]; i <= ranges[r+1]; i++)
					set.Add(i);
			return set;
		}

		/// <summary>Initializes BaseLexer.</summary>
		/// <param name="chars">A source of characters, e.g. <see cref="UString"/>.</param>
		/// <param name="fileName">A file name associated with the characters, 
		/// which will be used for error reporting.</param>
		/// <param name="inputPosition">A location to start lexing (normally 0).
		/// Careful: If you're starting to lex in the middle of the file, the 
		/// <see cref="LineNumber"/> still starts at 1, and (if <c>newSourceFile</c>
		/// is true) the <see cref="SourceFile"/> object may or may not discover 
		/// line breaks prior to the starting point, depending on how it is used.</param>
		/// <param name="newSourceFile">Whether to create a <see cref="LexerSourceFile{C}"/>
		/// object (an implementation of <see cref="ISourceFile"/>) to keep track 
		/// of line boundaries. The <see cref="SourceFile"/> property will point
		/// to this object, and it will be null if this parameter is false. Using 
		/// 'false' will avoid memory allocation, but prevent you from mapping 
		/// character positions to line numbers and vice versa. However, this
		/// object will still keep track of the current <see cref="LineNumber"/> 
		/// and <see cref="LineStartAt"/> (the index where the current line started) 
		/// when this parameter is false.</param>
		public BaseLexer(CharSrc chars, string fileName = "", int inputPosition = 0, bool newSourceFile = true)
		{
			Reset(chars, fileName, inputPosition, newSourceFile);
		}

		/// <summary>Reinitializes the object. This method is called by the constructor.</summary>
		/// <remarks>
		/// See the constructor for documentation of the parameters.
		/// <para/>
		/// This method can be used to avoid memory allocations when you
		/// need to parse many small strings in a row. If that's your goal, you 
		/// should set the <c>newSourceFile</c> parameter to false if possible.</remarks>
		public virtual void Reset(CharSrc chars, string fileName = "", int inputPosition = 0, bool newSourceFile = true)
		{
			CheckParam.IsNotNull<object>("source", chars);
			_charSource = chars;
			_fileName = fileName;
			_block = UString.Empty;
			InputPosition = inputPosition;
			_lineNumber = 1;
			_lineStartAt = inputPosition;
			if (newSourceFile)
				_sourceFile = new LexerSourceFile<CharSrc>(chars, fileName);
			else
				_sourceFile = null;
		}
		protected void Reset()
		{
			Reset(CharSource, FileName, 0, SourceFile != null);
		}

		/// <summary>Throws FormatException when it receives an error. Non-errors
		/// are sent to <see cref="MessageSink.Current"/>.</summary>
		public static readonly IMessageSink FormatExceptionErrorSink = MessageSink.FromDelegate(
			(sev, location, fmt, args) => { 
				if (sev >= Severity.Error)
					throw new FormatException(MessageSink.LocationString(location) + ": " + Localize.From(fmt, args));
				else
					MessageSink.Current.Write(sev, location, fmt, args);
			});

		private IMessageSink _errorSink;
		/// <summary>Gets or sets the object to which error messages are sent. The
		/// default object is <see cref="FormatExceptionErrorSink"/>, which throws
		/// <see cref="FormatException"/> if an error occurs.</summary>
		public IMessageSink ErrorSink
		{
			get { return _errorSink ?? FormatExceptionErrorSink; }
			set { _errorSink = value; }
		}

		protected int LA0 { get; private set; }
		private CharSrc _charSource;
		protected CharSrc CharSource
		{
			get { return _charSource; }
		}

		private string _fileName;
		protected string FileName 
		{ 
			get { return _fileName; }
		}

		private int _inputPosition = 0;
		public int InputPosition
		{
			get { return _inputPosition; }
			#if DotNet45
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			#endif
			protected set
			{
				_inputPosition = value;
				bool fail;
				LA0 = _block.TryGet(value - _blockStart, out fail);
				if (fail)
					ReadBlock();
			}
		}

		// A small region of the ICharSource that is cached so we can access it 
		// without virtual function calls (as lexers are speed-critical).
		UString _block;
		int _blockStart;
		protected int CachedBlockSize = 128;

		private void ReadBlock()
		{
			_block = _charSource.Slice(_inputPosition, CachedBlockSize);
			_blockStart = _inputPosition;
			bool fail;
			LA0 = _block.TryGet(0, out fail);
			if (fail)
				LA0 = -1;
		}

		LexerSourceFile<CharSrc> _sourceFile;
		protected LexerSourceFile<CharSrc> SourceFile
		{
			get { return _sourceFile; }
		}

		protected int LA(int i)
		{
			bool fail;
			int result = _block.TryGet(_inputPosition + i - _blockStart, out fail);
			return fail ? LA_slow(i) : result;
		}
		int LA_slow(int i)
		{
			bool fail;
			int result = _charSource.TryGet(_inputPosition + i, out fail);
			return fail ? -1 : result;
		}

		/// <summary>Increments InputPosition. Called by LLLPG when prediction 
		/// already verified the input (and caller doesn't save LA(0))</summary>
		#if DotNet45
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		#endif
		protected void Skip()
		{
			Debug.Assert(_inputPosition <= _charSource.Count);
			InputPosition++;
		}

		protected int _lineStartAt;
		protected int _lineNumber = 1;
		
		/// <summary>Current line number. Starts at 1 for the first line, unless derived class changes it.</summary>
		public int LineNumber { get { return _lineNumber; } }

		/// <summary>Index at which the current line started.</summary>
		protected int LineStartAt { get { return _lineStartAt; } }

		/// <summary>The lexer must call this method exactly once after it advances 
		/// past each newline, even inside comments and strings. This method keeps
		/// the <see cref="LineNumber"/> and <see cref="LineStartAt"/> properties
		/// updated.</summary>
		protected virtual void AfterNewline()
		{
			_lineStartAt = InputPosition;
			_lineNumber++;
			if (_sourceFile != null)
				_sourceFile.AfterNewline(InputPosition);
		}

		/// <summary>Default newline parser that matches '\n' or '\r' unconditionally.</summary>
		/// <remarks>
		/// You can use this implementation in an LLLPG lexer with "extern", like so:
		/// <c>extern rule Newline @[ '\r' + '\n'? | '\n' ];</c>
		/// By using this implementation everywhere in the grammar in which a 
		/// newline is allowed (even inside comments and strings), you can ensure
		/// that <see cref="AfterNewline()"/> is called, so that the line number
		/// is updated properly.
		/// </remarks>
		protected void Newline()
		{
			int la0;
			la0 = LA0;
			if (la0 == '\r') {
				Skip();
				for (;;) {
					la0 = LA0;
					if (la0 == '\r')
						Skip();
					else
						break;
				}
				la0 = LA0;
				if (la0 == '\n')
					Skip();
			} else
				Match('\n');
			AfterNewline();
		}

		/// <summary>Skips past any spaces at the current position. Equivalent to
		/// <c>rule Spaces @[ (' '|'\t')* ]</c> in LLLPG.</summary>
		protected void Spaces()
		{
			for (;;) {
				if (LA0 == '\t' || LA0 == ' ')
					Skip();
				else
					break;
			}
		}

		#region Normal matching

		protected int MatchAny()
		{
			int la = LA0;
			InputPosition++;
			return la;
		}
		protected int Match(HashSet<int> set)
		{
			int la = LA0;
			if (!set.Contains(la)) {
				Error(false, set);
			} else
				InputPosition++;
			return la;
		}
		#if DotNet45
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		#endif
		protected int Match(int a)
		{
			int la = LA0;
			if (la == a)
				InputPosition++;
			else
				Error(false, a, a);
			return la;
		}
		protected int Match(int a, int b)
		{
			int la = LA0;
            if (la != a && la != b)
				Error(false, a, a, b, b);
			else
				InputPosition++;
			return la;
		}
		protected int Match(int a, int b, int c)
		{
			int la = LA0;
			if (la != a && la != b && la != c)
				Error(false, a, a, b, b, c, c);
			else
				InputPosition++;
			return la;
		}
		protected int Match(int a, int b, int c, int d)
		{
			int la = LA0;
			if (la != a && la != b && la != c && la != d)
				Error(false, a, a, b, b, c, c, d, d);
			else
				InputPosition++;
			return la;
		}
		protected int MatchRange(int aLo, int aHi)
		{
			int la = LA0;
			if ((la < aLo || la > aHi))
				Error(false, aLo, aHi);
			else
				InputPosition++;
			return la;
		}
		protected int MatchRange(int aLo, int aHi, int bLo, int bHi)
		{
			int la = LA0;
			if ((la < aLo || la > aHi) && (la < bLo || la > bHi))
				Error(false, aLo, aHi, bLo, bHi);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExcept()
		{
			int la = LA0;
			if (la == -1)
				Error(true, -1, -1);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExcept(HashSet<int> set)
		{
			int la = LA0;
			if (set.Contains(la))
				Error(true, set);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExcept(int a)
		{
			int la = LA0;
			if (la == -1 || la == a)
				Error(true, a, a);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExcept(int a, int b)
		{
			int la = LA0;
			if (la == -1 || la == a || la == b)
				Error(true, a, a, b, b);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExcept(int a, int b, int c)
		{
			int la = LA0;
			if (la == -1 || la == a || la == b || la == c)
				Error(true, a, a, b, b, c, c);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExcept(int a, int b, int c, int d)
		{
			int la = LA0;
			if (la == -1 || la == a || la == b || la == c || la == d)
				Error(true, a, a, b, b, c, c, d, d);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExceptRange(int aLo, int aHi)
		{
			int la = LA0;
			if (la == -1 || (la >= aLo && la <= aHi))
				Error(true, aLo, aHi);
			else
				InputPosition++;
			return la;
		}
		protected int MatchExceptRange(int aLo, int aHi, int bLo, int bHi)
		{
			int la = LA0;
			if (la == -1 || (la >= aLo && la <= aHi) || (la >= bLo && la <= bHi))
				Error(true, aLo, aHi, bLo, bHi);
			else
				InputPosition++;
			return la;
		}

		#endregion

		#region Try-matching

		/// <summary>A helper class used by LLLPG for backtracking.</summary>
		public struct SavePosition : IDisposable
		{
			BaseLexer<CharSrc> _lexer;
			int _oldPosition;
			public SavePosition(BaseLexer<CharSrc> lexer, int lookaheadAmt)
				{ _lexer = lexer; _oldPosition = lexer.InputPosition; lexer.InputPosition += lookaheadAmt; }
			public void Dispose() { _lexer.InputPosition = _oldPosition; }
		}
		protected bool TryMatch(HashSet<int> set)
		{
			if (!set.Contains(LA0))
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatch(int a)
		{
			if (LA0 != a)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatch(int a, int b)
		{
			int la = LA0;
			if (la != a && la != b)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatch(int a, int b, int c)
		{
			int la = LA0;
			if (la != a && la != b && la != c)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatch(int a, int b, int c, int d)
		{
			int la = LA0;
			if (la != a && la != b && la != c && la != d)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchRange(int aLo, int aHi)
		{
			int la = LA0;
			if (la < aLo || la > aHi)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchRange(int aLo, int aHi, int bLo, int bHi)
		{
			int la = LA0;
			if ((la < aLo || la > aHi) && (la < bLo || la > bHi))
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExcept()
		{
			if (LA0 == -1)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExcept(HashSet<int> set)
		{
			if (set.Contains(LA0))
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExcept(int a)
		{
			int la = LA0;
			if (la == -1 || la == a)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExcept(int a, int b)
		{
			int la = LA0;
			if (la == -1 || la == a || la == b)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExcept(int a, int b, int c)
		{
			int la = LA0;
			if (la == -1 || la == a || la == b || la == c)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExcept(int a, int b, int c, int d)
		{
			int la = LA0;
			if (la == -1 || la == a || la == b || la == c || la == d)
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExceptRange(int aLo, int aHi)
		{
			int la = LA0;
			if (la == -1 || (la >= aLo && la <= aHi))
				return false;
			else
				InputPosition++;
			return true;
		}
		protected bool TryMatchExceptRange(int aLo, int aHi, int bLo, int bHi)
		{
			int la = LA0;
			if (la == -1 || (la >= aLo && la <= aHi) || (la >= bLo && la <= bHi))
				return false;
			else
				InputPosition++;
			return true;
		}

		#endregion

		protected virtual void Check(bool expectation, string expectedDescr = "")
		{
			if (!expectation)
				Error(0, "An expected condition was false: {0}", expectedDescr);
		}

		/// <summary>This method is called to handle errors that occur during lexing.</summary>
		/// <param name="lookaheadIndex">Index where the error occurred, relative to
		/// the current InputPosition (i.e. InputPosition + lookaheadIndex is the
		/// position of the error).</param>
		/// <param name="message">An error message, not including the error location.</param>
		protected virtual void Error(int lookaheadIndex, string message)
		{
			Error(lookaheadIndex, message, EmptyArray<object>.Value);
		}
		
		/// <summary>This method is called to format and handle errors that occur 
		/// during lexing. The default implementation sends errors to <see cref="ErrorSink"/>, 
		/// which, by default, throws a <see cref="FormatException"/>.</summary>
		/// <param name="lookaheadIndex">Index where the error occurred, relative to
		/// the current InputPosition (i.e. InputPosition + lookaheadIndex is the
		/// position of the error).</param>
		/// <param name="format">An error description with argument placeholders.</param>
		/// <param name="args">Arguments to insert into the error message.</param>
		protected virtual void Error(int lookaheadIndex, string format, params object[] args)
		{
			SourcePos pos = IndexToLine(InputPosition + lookaheadIndex);
			ErrorSink.Write(Severity.Error, pos, format, args);
		}

		protected virtual void Error(bool inverted, int range0lo, int range0hi) { Error(inverted, new int[] { range0lo, range0hi }); }
		protected virtual void Error(bool inverted, params int[] ranges) { Error(inverted, (IList<int>)ranges); }
		protected virtual void Error(bool inverted, IList<int> ranges)
		{
			string rangesDescr = RangesToString(ranges);
			var input = new StringBuilder();
			PrintChar(LA0, input);
			if (inverted)
				Error(0, "{0}: expected a character other than {1}", input, rangesDescr);
			else if (ranges.Count > 2)
				Error(0, "{0}: expected one of {1}", input, rangesDescr);
			else
				Error(0, "{0}: expected {1}", input, rangesDescr);
		}
		protected virtual void Error(bool inverted, HashSet<int> set)
		{
			var array = set.ToList();
			array.Sort();
			var list = new List<int>();
			int i, j;
			for (i = 0; i < array.Count; i++)
			{
				for (j = i + 1; j < array.Count && array[j] == array[i] + 1; j++) { }
				list.Add(i);
				list.Add(j - 1);
			}
			Error(inverted, list);
		}

		/// <summary>Converts a list of character ranges to a string, e.g. for input
		/// list {'*','*','a','z'}, the output is "'*' 'a'..'z'".</summary>
		protected string RangesToString(IList<int> ranges)
		{
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < ranges.Count; i += 2)
			{
				if (i != 0)
					sb.Append(' ');
				int lo = ranges[i], hi = ranges[i + 1];
				PrintChar(lo, sb);
				if (hi > lo)
				{
					sb.Append(hi > lo + 1 ? ".." : " ");
					PrintChar(hi, sb);
				}
			}
			return sb.ToString();
		}
		
		/// <summary>Prints a character as a string, e.g. <c>'a' -> "'a'"</c>, with 
		/// the special value -1 representing EOF, so PrintChar(-1, ...) == "EOF".</summary>
		protected void PrintChar(int c, StringBuilder sb)
		{
			if (c == -1)
				sb.Append("EOF");
			else if (c >= 0 && c < 0xFFFC) {
				sb.Append('\'');
				G.EscapeCStyle((char)c, sb, EscapeC.Default | EscapeC.SingleQuotes);
				sb.Append('\'');
			} else
				sb.Append(c);
		}

		public SourcePos IndexToLine(int index)
		{
			if (SourceFile == null)
				return new SourcePos(_fileName, LineNumber, index - _lineStartAt + 1);
			else
				return SourceFile.IndexToLine(index);
		}
	}

	/// <summary>Alias for <see cref="BaseLexer{C}"/> where C is <see cref="ICharSource"/>.</summary>
	public abstract class BaseLexer : BaseLexer<ICharSource>
	{
		protected BaseLexer(ICharSource source, string fileName = "", int inputPosition = 0, bool newSourceFile = true) : base(source, fileName, inputPosition, newSourceFile) { }
	}
}
