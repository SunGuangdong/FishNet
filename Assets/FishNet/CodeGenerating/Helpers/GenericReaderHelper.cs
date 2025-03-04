﻿using FishNet.CodeGenerating.Helping.Extension;
using FishNet.Serializing;
using MonoFN.Cecil;
using MonoFN.Cecil.Cil;
using MonoFN.Cecil.Rocks;
using System;
using System.Collections.Generic;

namespace FishNet.CodeGenerating.Helping
{

    internal class GenericReaderHelper : CodegenBase
    {

        #region Reflection references.
        private TypeReference _genericReaderTypeRef;
        private TypeReference _readerTypeRef;
        private MethodReference _readSetMethodRef;
        private MethodReference _readAutoPackSetMethodRef;
        private TypeDefinition _generatedReaderWriterClassTypeDef;
        private MethodDefinition _generatedReaderWriterOnLoadMethodDef;
        private TypeReference _autoPackTypeRef;
        #endregion

        #region Misc.
        /// <summary>
        /// TypeReferences which have already had delegates made for.
        /// </summary>
        private HashSet<TypeReference> _delegatedTypes = new HashSet<TypeReference>();
        #endregion

        #region Const.
        public const string INITIALIZEONCE_METHOD_NAME = GenericWriterHelper.INITIALIZEONCE_METHOD_NAME;
        public const MethodAttributes INITIALIZEONCE_METHOD_ATTRIBUTES = GenericWriterHelper.INITIALIZEONCE_METHOD_ATTRIBUTES;
        public const MethodAttributes GENERATED_METHOD_ATTRIBUTES = GenericWriterHelper.GENERATED_METHOD_ATTRIBUTES;
        #endregion

        /// <summary>
        /// Imports references needed by this helper.
        /// </summary>
        /// <param name="moduleDef"></param>
        /// <returns></returns>
        public override bool ImportReferences()
        {
            _genericReaderTypeRef = base.ImportReference(typeof(GenericReader<>));
            _readerTypeRef = base.ImportReference(typeof(Reader));
            _autoPackTypeRef = base.ImportReference(typeof(AutoPackType));

            System.Reflection.PropertyInfo readPropertyInfo;
            readPropertyInfo = typeof(GenericReader<>).GetProperty(nameof(GenericReader<int>.Read));
            _readSetMethodRef = base.ImportReference(readPropertyInfo.GetSetMethod());
            readPropertyInfo = typeof(GenericReader<>).GetProperty(nameof(GenericReader<int>.ReadAutoPack));
            _readAutoPackSetMethodRef = base.ImportReference(readPropertyInfo.GetSetMethod());

            return true;
        }

        /// <summary>
        /// Creates a Read delegate for readMethodRef and places it within the generated reader/writer constructor.
        /// </summary>
        /// <param name="readMr"></param>
        /// <param name="diagnostics"></param>
        internal void CreateReadDelegate(MethodReference readMr, bool isStatic)
        {
            if (!isStatic)
            {
                //Supporting Write<T> with types containing generics is more trouble than it's worth.
                if (readMr.IsGenericInstance || readMr.HasGenericParameters)
                    return;
            }

            GeneralHelper gh = base.GetClass<GeneralHelper>();
            bool created;
            /* If class for generated reader/writers isn't known yet.
            * It's possible this is the case if the entry being added
            * now is the first entry. That would mean the class was just
            * generated. */
            if (_generatedReaderWriterClassTypeDef == null)
                _generatedReaderWriterClassTypeDef = base.GetClass<GeneralHelper>().GetOrCreateClass(out _, ReaderGenerator.GENERATED_TYPE_ATTRIBUTES, ReaderGenerator.GENERATED_READERS_CLASS_NAME, null, GenericWriterHelper.GENERATED_WRITER_NAMESPACE);
            /* If constructor isn't set then try to get or create it
             * and also add it to methods if were created. */
            if (_generatedReaderWriterOnLoadMethodDef == null)
            {
                _generatedReaderWriterOnLoadMethodDef = base.GetClass<GeneralHelper>().GetOrCreateMethod(_generatedReaderWriterClassTypeDef, out created, INITIALIZEONCE_METHOD_ATTRIBUTES, INITIALIZEONCE_METHOD_NAME, base.Module.TypeSystem.Void);
                if (created)
                    gh.CreateRuntimeInitializeOnLoadMethodAttribute(_generatedReaderWriterOnLoadMethodDef);
            }
            //Check if ret already exist, if so remove it; ret will be added on again in this method.
            if (_generatedReaderWriterOnLoadMethodDef.Body.Instructions.Count != 0)
            {
                int lastIndex = (_generatedReaderWriterOnLoadMethodDef.Body.Instructions.Count - 1);
                if (_generatedReaderWriterOnLoadMethodDef.Body.Instructions[lastIndex].OpCode == OpCodes.Ret)
                    _generatedReaderWriterOnLoadMethodDef.Body.Instructions.RemoveAt(lastIndex);
            }
            //Check if already exist.
            ILProcessor processor = _generatedReaderWriterOnLoadMethodDef.Body.GetILProcessor();
            TypeReference dataTypeRef = readMr.ReturnType;
            if (_delegatedTypes.Contains(dataTypeRef))
            {
                base.LogError($"Generic read already created for {dataTypeRef.FullName}.");
                return;
            }
            else
            {
                _delegatedTypes.Add(dataTypeRef);
            }



            //Create a Func<Reader, T> delegate 
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, readMr);

            GenericInstanceType functionGenericInstance;
            MethodReference functionConstructorInstanceMethodRef;
            bool isAutoPacked = base.GetClass<ReaderHelper>().IsAutoPackedType(dataTypeRef);

            //Generate for autopacktype.
            if (isAutoPacked)
            {
                functionGenericInstance = gh.FunctionT3TypeRef.MakeGenericInstanceType(_readerTypeRef, _autoPackTypeRef, dataTypeRef);
                functionConstructorInstanceMethodRef = gh.FunctionT3ConstructorMethodRef.MakeHostInstanceGeneric(base.Session, functionGenericInstance);
            }
            //Not autopacked.
            else
            {
                functionGenericInstance = gh.FunctionT2TypeRef.MakeGenericInstanceType(_readerTypeRef, dataTypeRef);
                functionConstructorInstanceMethodRef = gh.FunctionT2ConstructorMethodRef.MakeHostInstanceGeneric(base.Session, functionGenericInstance);
            }
            processor.Emit(OpCodes.Newobj, functionConstructorInstanceMethodRef);

            //Call delegate to GeneratedReader<T>.Read
            GenericInstanceType genericInstance = _genericReaderTypeRef.MakeGenericInstanceType(dataTypeRef);
            MethodReference genericReadMethodRef = (isAutoPacked) ?
                    _readAutoPackSetMethodRef.MakeHostInstanceGeneric(base.Session, genericInstance) :
                    _readSetMethodRef.MakeHostInstanceGeneric(base.Session, genericInstance);
            processor.Emit(OpCodes.Call, genericReadMethodRef);

            processor.Emit(OpCodes.Ret);
        }


    }
}