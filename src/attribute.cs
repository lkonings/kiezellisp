// Copyright (C) 2012-2013 Jan Tolenaar. See the file LICENSE for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Kiezel
{
    public partial class Runtime
    {

        // Handled by compiler if used as function in function call; otherwise
        // accessor creates a lambda.
        [Lisp( "." )]
        public static object MemberAccessor( string member )
        {
            return new AccessorLambda( member );
        }

        [Lisp( "%attr" )]
        public static object Attr( object target, object attr )
        {
            if ( target is Prototype )
            {
                var proto = ( Prototype ) target;
                var result = proto.GetValue( attr );
                return result;
            }
            else
            {
                var name = GetDesignatedString( attr );
                var binder = GetGetMemberBinder( name );
                var code = CompileDynamicExpression( binder, typeof( object ), new Expression[] { Expression.Constant( target ) } );
                var result = Execute( code );
                return result;
            }
        }

        [Lisp( "%set-attr" )]
        public static object SetAttr( object target, object attr, object value )
        {
            if ( target is Prototype )
            {
                var proto = ( Prototype ) target;
                return proto.SetValue( attr, value );
            }
            else
            {
                var name = GetDesignatedString( attr );
                var binder = GetSetMemberBinder( name );
                var code = CompileDynamicExpression( binder, typeof( object ), new Expression[] { Expression.Constant( target ), Expression.Constant( value ) } );
                var result = Execute( code );
                return result;
            }
        }

    }
}
