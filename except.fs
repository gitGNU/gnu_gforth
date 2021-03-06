\ catch, throw, etc.

\ Copyright (C) 1999,2000,2003,2006,2007,2010,2013,2014,2015,2016 Free Software Foundation, Inc.

\ This file is part of Gforth.

\ Gforth is free software; you can redistribute it and/or
\ modify it under the terms of the GNU General Public License
\ as published by the Free Software Foundation, either version 3
\ of the License, or (at your option) any later version.

\ This program is distributed in the hope that it will be useful,
\ but WITHOUT ANY WARRANTY; without even the implied warranty of
\ MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
\ GNU General Public License for more details.

\ You should have received a copy of the GNU General Public License
\ along with this program. If not, see http://www.gnu.org/licenses/.

\ !! use a separate exception stack?           anton

\ has? backtrace [IF]
Defer store-backtrace
' noop IS store-backtrace
\ [THEN]

\ Ok, here's the story about how we get to the native code for the
\ recovery code in case of a THROW, and why there is all this funny
\ stuff being compiled by TRY and RECOVER:

\ Upon a THROW, we cannot just return through the ordinary return
\ address, but have to use a different one, for code after the
\ RECOVER.  How do we do that, in a way portable between the various
\ threaded and native code engines?  In particular, how does the
\ native code engine learn about the address of the native recovery
\ code?

\ On the Forth level, we can compile only references to threaded code.
\ The only thing that translates a threaded code address to a native
\ code address is docol, which is only called with EXECUTE and
\ friends.  So we start the recovery code with a docol, and invoke it
\ with PERFORM; the recovery code then rdrops the superfluously
\ generated return address and continues with the proper recovery
\ code.

\ At compile time, since we cannot compile a forward reference (to the
\ recovery code) as a literal (backpatching does not work for
\ native-code literals), we produce a data cell (wrapped in AHEAD
\ ... THEN) that we can backpatch, and compile the address of that as
\ literal.

\ Overall, this leads to the following resulting code:

\   ahead
\ +><recovery address>-+
\ | then               |
\ +-lit                |
\   (try)              |
\   ...                |
\   (recover)          |
\   ahead              |
\   docol: <-----------+
\   rdrop
\   ...
\   then
\   ...

\ !! explain handler on-stack structure

User first-throw  \ contains true if the next throw is the first throw
User stored-backtrace ( addr -- )
\ contains the address of a cell-counted string that contains a copy
\ of the return stack at the throw

: nothrow ( -- ) \ gforth
    \G Use this (or the standard sequence @code{['] false catch 2drop})
    \G after a @code{catch} or @code{endtry} that does not rethrow;
    \G this ensures that the next @code{throw} will record a
    \G backtrace.
    first-throw on ;

' nothrow is .status

: (try0) ( -- aoldhandler )
    nothrow handler @ ;

[undefined] (try1) [if]
: (try1) ( aoldhandler arecovery -- anewhandler )
    r>
    swap >r \ recovery address
    sp@ cell+ >r
    o#+ [ 0 , ] >r
    fp@ >r
    lp@ >r
    swap >r \ old handler
    rp@ swap \ new handler
    >r ;
[endif]

: (try2)
    handler ! ;

: (try) ( ahandler -- )
    nothrow
    r>
    swap >r \ recovery address
    sp@ >r
    o#+ [ 0 , ] >r
    fp@ >r
    lp@ >r
    handler @ >r
    rp@ handler !
    >r ;

\ : try ( compilation  -- orig ; run-time  -- R:sys1 ) \ gforth
\     \G Start an exception-catching region.
\     POSTPONE ahead here >r >mark 1 cs-roll POSTPONE then
\     r> POSTPONE literal POSTPONE (try) ; immediate compile-only

: try ( compilation  -- orig ; run-time  -- R:sys1 ) \ gforth
    \G Start an exception-catching region.
    POSTPONE ahead here >r >mark 1 cs-roll POSTPONE then
    POSTPONE (try0) r> POSTPONE literal POSTPONE (try1) POSTPONE (try2)
; immediate compile-only


: uncatch ( -- ) \ gforth
    \g unwind an exception frame
    r>
    r> handler !
    rdrop \ lp
    rdrop \ fp
    rdrop \ op
    rdrop \ sp
    rdrop \ recovery address
    >r ;

: handler-intro, ( -- )
    docol: here 0 , 0 , code-address! \ start a colon def 
    postpone rdrop                    \ drop the return address
;

: iferror ( compilation  orig1 -- orig2 ; run-time  -- ) \ gforth
    \G Starts the exception handling code (executed if there is an
    \G exception between @code{try} and @code{endtry}).  This part has
    \G to be finished with @code{then}.
    \ !! check using a special tag
    POSTPONE else handler-intro,
; immediate compile-only

: restore ( compilation  orig1 -- ; run-time  -- ) \ gforth
    \G Starts restoring code, that is executed if there is an
    \G exception, and if there is no exception.
    POSTPONE iferror POSTPONE then
; immediate compile-only

: endtry ( compilation  -- ; run-time  R:sys1 -- ) \ gforth
    \G End an exception-catching region.
    POSTPONE uncatch
; immediate compile-only

: endtry-iferror ( compilation  orig1 -- orig2 ; run-time  R:sys1 -- ) \ gforth
    \G End an exception-catching region while starting
    \G exception-handling code outside that region (executed if there
    \G is an exception between @code{try} and @code{endtry-iferror}).
    \G This part has to be finished with @code{then} (or
    \G @code{else}...@code{then}).
    POSTPONE uncatch POSTPONE iferror POSTPONE uncatch
; immediate compile-only

0 Value catch-frame

:noname ( x1 .. xn xt -- y1 .. ym 0 / z1 .. zn error ) \ exception
    try
	execute [ here to catch-frame ] 0
    iferror
	nip
    then endtry ;
is catch

[undefined] (throw1) [if]
: (throw1) ( ... ball frame -- ... ball )
    dup rp! ( ... ball frame )
    cell+ dup @ lp!
    cell+ dup @ fp!
    cell+ dup @ >o rdrop
    cell+ dup @ ( ... ball addr sp ) -rot 2>r sp! drop 2r>
    cell+ @ perform ;
[endif]

Defer kill-task ' noop IS kill-task
Variable located-xpos
Variable located-len
variable bn-xpos      \ first contains located-xpos, but is updated by B and N
variable located-top  \ first line to display with l
variable located-bottom \ last line to display with l
2variable located-slurped \ the contents of the file in located-xpos, or 0 0

\ lines to show before and after locate
3 value before-locate
12 value after-locate

: xpos>file# ( xpos -- u )
    23 rshift ;

: xpos>line ( xpos -- u )
    8 rshift $7fff and ;

: set-located-xpos ( xpos len -- )
    over xpos>file# located-xpos @ xpos>file# <> if
	located-slurped 2@ drop ?dup-if
	    free throw then
	0 0 located-slurped 2! then
    located-len ! dup located-xpos ! dup bn-xpos !
    xpos>line
    dup before-locate - 0 max located-top !
    after-locate + located-bottom ! ;

: set-current-xpos ( -- )
    current-sourcepos1 input-lexeme @ set-located-xpos ;

[IFDEF] ?set-current-xpos
    :noname error-stack $@len 0= IF  set-current-xpos  THEN ;
    is ?set-current-xpos
[THEN]

\ : set-current-xpos ( -- )
\    input-lexeme @ located-len ! current-sourcepos1 located-xpos ! ;

:noname ( y1 .. ym error/0 -- y1 .. ym / z1 .. zn error ) \ exception
    ?DUP IF
	[ here forthstart 9 cells + !
	  here throw-entry ! ]
	first-throw @ IF
	    store-backtrace
	THEN
	handler @ ?dup-0=-IF
	    >stderr cr ." uncaught exception: " .error cr
	    kill-task  2 (bye)
	THEN
        (throw1)
    THEN ;
is throw

\ throw is heavy-weight due to error handling;
\ if you want to use throw as control structure, use fast-throw
: fast-throw ( n -- ) handler @ (throw1) ;
