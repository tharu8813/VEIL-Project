// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Veil.VolumeBroker;

internal static class CefSignedSystemModuleBaselineTests
{
    internal static void Run()
    {
        using var signing = RSA.Create(3072);
        using var wrongKey = RSA.Create(3072);
        using var weakKey = RSA.Create(2048);
        byte[] publicKey = signing.ExportSubjectPublicKeyInfo();
        var host = new CefSystemBaselineHost("x64", 26100, 1234);
        var module = new { name = "kernel32.dll", bytes = 123L,
            sha256 = new string('a', 64), signerSha256 = new string('b', 64) };
        byte[] Payload(int build = 26100, int revision = 1234, long sequence = 7,
            string architecture = "x64", object? modules = null) =>
            JsonSerializer.SerializeToUtf8Bytes(new { schema = "veil-cef-system-modules-v1",
                architecture, build, updateRevision = revision, sequence,
                modules = modules ?? new[] { module } });
        byte[] Sign(byte[] data) => signing.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        void Reject(Action action)
        {
            try { action(); }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Invalid signed system baseline accepted.");
        }
        void RejectSigned(byte[] data) =>
            Reject(() => CefSignedSystemModuleBaseline.Verify(data, Sign(data), publicKey, host, 7));

        byte[] good = Payload();
        byte[] signature = Sign(good);
        using (var input = new MemoryStream(good, writable: false))
        {
            input.Position = 1;
            byte[] snapshot = CefSignedSystemModuleBaseline.ReadBoundedInput(input, 1, 1024 * 1024);
            if (!snapshot.AsSpan().SequenceEqual(good))
                throw new InvalidOperationException("Baseline snapshot changed exact signed bytes.");
            _ = CefSignedSystemModuleBaseline.Verify(snapshot, signature, publicKey, host, 7);
        }
        foreach (int length in new[] { 0, 1024 * 1024 + 1 })
        {
            using var invalid = new MemoryStream(new byte[length]);
            Reject(() => CefSignedSystemModuleBaseline.ReadBoundedInput(invalid, 1, 1024 * 1024));
        }
        foreach (int length in new[] { 383, 513 })
        {
            using var invalid = new MemoryStream(new byte[length]);
            Reject(() => CefSignedSystemModuleBaseline.ReadBoundedInput(invalid, 384, 512));
        }
        using (var detached = new MemoryStream(signature, writable: false))
        {
            if (!CefSignedSystemModuleBaseline.ReadBoundedInput(detached, 384, 512).AsSpan().SequenceEqual(signature))
                throw new InvalidOperationException("Detached signature snapshot changed.");
        }
        Console.WriteLine("PASS: exact bounded baseline input snapshots and length refusal");
        _ = CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 7);
        byte[] changed = (byte[])good.Clone();
        changed[^2] ^= 1;
        Reject(() => CefSignedSystemModuleBaseline.Verify(changed, signature, publicKey, host, 7));
        byte[] changedSignature = (byte[])signature.Clone();
        changedSignature[0] ^= 1;
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, changedSignature, publicKey, host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, wrongKey.ExportSubjectPublicKeyInfo(), host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, [.. publicKey, 0], host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good,
            weakKey.SignData(good, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            weakKey.ExportSubjectPublicKeyInfo(), host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good,
            signing.SignData(good, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            publicKey, host, 7));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 8));
        Reject(() => CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 0));
        RejectSigned(Payload(build: 26101));
        RejectSigned(Payload(revision: 1235));
        RejectSigned(Payload(architecture: "arm64"));
        RejectSigned(Payload(sequence: 6));
        RejectSigned(Payload(modules: Array.Empty<object>()));
        RejectSigned(Payload(modules: new[] { module, module }));
        RejectSigned(Payload(modules: new[] { module, new { name = "KERNEL32.DLL",
            bytes = module.bytes, sha256 = module.sha256, signerSha256 = module.signerSha256 } }));
        RejectSigned(Payload(modules: new[] { new { name = module.name,
            bytes = module.bytes, sha256 = module.sha256, signerSha256 = "bad" } }));
        RejectSigned(Payload(modules: new[] { new { name = module.name,
            bytes = module.bytes, sha256 = module.sha256 } }));
        string json = Encoding.UTF8.GetString(good);
        RejectSigned(Encoding.UTF8.GetBytes(json.Insert(1, "\"sequence\":7,")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Insert(1, "\"publicKey\":\"untrusted\",")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Replace("\"name\":", "\"name\":\"duplicate.dll\",\"name\":")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Replace("\"sequence\":7", "\"sequence\":\"7\"")));
        RejectSigned(Encoding.UTF8.GetBytes(json.Replace("veil-cef-system-modules-v1", "other-schema")));
        RejectSigned(Encoding.UTF8.GetBytes(json[..^1]));
        RejectSigned(new byte[1024 * 1024 + 1]);
        var observed = CefSystemBaselineHostObserver.Reconcile(26100, "26100",
            RegistryValueKind.String, 1234, RegistryValueKind.DWord);
        if (observed != host) throw new InvalidOperationException("Host reconciliation regression.");
        Reject(() => CefSystemBaselineHostObserver.Reconcile(26100, "26101",
            RegistryValueKind.String, 1234, RegistryValueKind.DWord));
        Reject(() => CefSystemBaselineHostObserver.Reconcile(26100, "026100",
            RegistryValueKind.String, 1234, RegistryValueKind.DWord));
        Reject(() => CefSystemBaselineHostObserver.Reconcile(26100, 26100,
            RegistryValueKind.DWord, 1234, RegistryValueKind.DWord));
        Reject(() => CefSystemBaselineHostObserver.Reconcile(26100, "26100",
            RegistryValueKind.String, "1234", RegistryValueKind.String));
        Reject(() => CefSystemBaselineHostObserver.Reconcile(26100, "26100",
            RegistryValueKind.String, -1, RegistryValueKind.DWord));
        Reject(() => CefSystemBaselineHostObserver.Reconcile(26100, "26100",
            RegistryValueKind.String, null, RegistryValueKind.DWord));
        int reads = 0;
        Reject(() => CefSystemBaselineHostObserver.RequireStable(() => ++reads == 1
            ? host : host with { UpdateRevision = 1235 }));
        int guardCalls = 0;
        var guarded = CefSignedSystemModuleBaseline.Verify(good, signature, publicKey, host, 7,
            () => { if (++guardCalls == 2) throw new UnauthorizedAccessException("Synthetic host change."); });
        Reject(() => guarded.RequireModules([]));
        if (guardCalls != 2) throw new InvalidOperationException("Host inventory guard not rechecked.");
        var actual = CefSystemBaselineHostObserver.Observe();
        var actualPayload = Payload(build: actual.Build, revision: actual.UpdateRevision);
        var realHostVerifier = CefSignedSystemModuleBaseline.VerifyCurrentHost(actualPayload,
            Sign(actualPayload), publicKey, 7);
        // Empty inventory exercises host binding only, not DLL trust.
        realHostVerifier.RequireModules([]);
        CefSystemBaselineTrustStateTests.Run(publicKey, actualPayload, Sign(actualPayload));
        var anchor = SHA256.HashData(publicKey);
        var enrollment = CefSystemBaselineTrustUpdate.PrepareEnrollment(good, signature, publicKey, anchor, 7, host);
        var enrolled = CefSystemBaselineTrustState.Parse(enrollment.RequireExpectedAndCopyRecord(null, host));
        enrollment.RequirePublished(enrolled, host);
        enrolled.RequirePayload(good);
        Reject(() => enrollment.RequireExpectedAndCopyRecord(enrolled, host));
        Reject(() => enrollment.RequireExpectedAndCopyRecord(null, host with { UpdateRevision = 1235 }));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareEnrollment(good, signature, publicKey, new byte[32], 7, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareEnrollment(good, signature, publicKey, anchor, 8, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareReplacement(enrolled, good, signature, host));
        byte[] sameSequence = Payload(modules: new[] { new { name = "other.dll", bytes = 123L,
            sha256 = module.sha256, signerSha256 = module.signerSha256 } });
        Reject(() => CefSystemBaselineTrustUpdate.PrepareReplacement(enrolled, sameSequence, Sign(sameSequence), host));
        byte[] older = Payload(sequence: 6);
        Reject(() => CefSystemBaselineTrustUpdate.PrepareReplacement(enrolled, older, Sign(older), host));
        byte[] nextPayload = Payload(sequence: 9);
        Reject(() => CefSystemBaselineTrustUpdate.PrepareReplacement(enrolled, nextPayload,
            wrongKey.SignData(nextPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host));
        var update = CefSystemBaselineTrustUpdate.PrepareReplacement(enrolled, nextPayload, Sign(nextPayload), host);
        var nextState = CefSystemBaselineTrustState.Parse(update.RequireExpectedAndCopyRecord(enrolled, host));
        if (nextState.MinimumSequence != 9 || !nextState.CopyPublicKey().SequenceEqual(publicKey))
            throw new InvalidOperationException("Baseline update sequence/key regression.");
        nextState.RequirePayload(nextPayload);
        Reject(() => nextState.RequirePayload(good));
        Reject(() => update.RequireExpectedAndCopyRecord(null, host));
        Reject(() => update.RequireExpectedAndCopyRecord(nextState, host));
        Reject(() => update.RequirePublished(enrolled, host));
        update.RequirePublished(nextState, host);
        void Expect(CefSystemBaselineTrustUpdate transition, bool exists,
            CefSystemBaselineTrustState? observedState, CefBaselinePublicationStatus expected)
        {
            if (transition.ClassifyPublication(exists, observedState, host) != expected)
                throw new InvalidOperationException("Interrupted publication classification regression.");
        }
        Expect(enrollment, false, null, CefBaselinePublicationStatus.NotStartedEnrollment);
        Expect(enrollment, true, null, CefBaselinePublicationStatus.PartialEnrollmentRequiresReview);
        Expect(enrollment, true, enrolled, CefBaselinePublicationStatus.IntendedRecordPresentUnconfirmed);
        Expect(enrollment, true, nextState, CefBaselinePublicationStatus.UnexpectedStateRequiresReview);
        Expect(update, false, null, CefBaselinePublicationStatus.UnexpectedStateRequiresReview);
        Expect(update, true, null, CefBaselinePublicationStatus.UnexpectedStateRequiresReview);
        Expect(update, true, enrolled, CefBaselinePublicationStatus.PreviousRecordPresent);
        Expect(update, true, nextState, CefBaselinePublicationStatus.IntendedRecordPresentUnconfirmed);
        var foreign = CefSystemBaselineTrustState.CreateVerifiedRecord(nextPayload,
            wrongKey.ExportSubjectPublicKeyInfo(), 9);
        Expect(update, true, foreign, CefBaselinePublicationStatus.UnexpectedStateRequiresReview);
        var sameVersionDifferentDigest = CefSystemBaselineTrustState.CreateVerifiedRecord(good, publicKey, 9);
        Expect(update, true, sameVersionDifferentDigest, CefBaselinePublicationStatus.UnexpectedStateRequiresReview);
        Reject(() => update.ClassifyPublication(false, nextState, host));
        Reject(() => update.ClassifyPublication(true, nextState, host with { UpdateRevision = 1235 }));
        Reject(() => update.RequirePublished(foreign, host));
        Reject(() => update.RequirePublished(sameVersionDifferentDigest, host));
        Console.WriteLine("PASS: interrupted baseline publication classification and exact confirmation refusal");
        // Synthetic independent approval fixture. Production must never
        // manufacture approval from an untrusted request in this manner.
        using var approvalKey = RSA.Create(3072);
        byte[] approvalPublic = approvalKey.ExportSubjectPublicKeyInfo();
        byte[] approvalAnchor = SHA256.HashData(approvalPublic);
        var authority = new CefBaselineApprovalAuthority(approvalPublic, approvalAnchor);
        authority.RequireFreshInstallation(false, false);
        Reject(() => authority.RequireFreshInstallation(true, false));
        Reject(() => authority.RequireFreshInstallation(false, true));
        Reject(() => authority.RequireFreshInstallation(true, true));
        byte[] authorityCopy = authority.CopyPublicKey();
        authorityCopy[0] ^= 1;
        if (!authority.CopyPublicKey().SequenceEqual(approvalPublic))
            throw new InvalidOperationException("Mutable approval authority key escaped.");
        Console.WriteLine("PASS: approval authority bootstrap-only policy and immutable key (synthetic)");
        authority.RequireSame(CefBaselineApprovalAuthority.ParseActiveRecord(approvalPublic));
        byte[] stoppedAuthority = authority.CreateStoppedRecord();
        authority.RequireStoppedRecord(stoppedAuthority);
        Reject(() => CefBaselineApprovalAuthority.ParseActiveRecord(stoppedAuthority));
        Reject(() => CefBaselineApprovalAuthority.ParseActiveRecord(stoppedAuthority[..8]));
        Reject(() => authority.RequireStoppedRecord(approvalPublic));
        byte[] tamperedStop = (byte[])stoppedAuthority.Clone();
        tamperedStop[^1] ^= 1;
        Reject(() => authority.RequireStoppedRecord(tamperedStop));
        var authorityTestState = CefSystemBaselineTrustState.CreateVerifiedRecord(actualPayload, publicKey, 7);
        byte[] currentAuthorityRecord = approvalPublic;
        var authorityBoundVerifier = CefSignedSystemModuleBaseline.VerifyWithApprovalSnapshot(
            actualPayload, Sign(actualPayload), authorityTestState, () => authorityTestState,
            authority, () => CefBaselineApprovalAuthority.ParseActiveRecord(currentAuthorityRecord));
        authorityBoundVerifier.RequireModules([]);
        authorityBoundVerifier.RequireBoundTrust();
        var rawVerifier = new CefSystemModuleVerifier([new CefSystemModulePin("kernel32.dll", 1,
            new string('A', 64), new string('B', 64))]);
        Reject(() => rawVerifier.RequireBoundTrust());
        currentAuthorityRecord = stoppedAuthority;
        Reject(() => authorityBoundVerifier.RequireBoundTrust());
        Reject(() => authorityBoundVerifier.RequireModules([]));
        Reject(() => CefSignedSystemModuleBaseline.VerifyWithApprovalSnapshot(actualPayload,
            Sign(actualPayload), authorityTestState, () => authorityTestState,
            authority, () => CefBaselineApprovalAuthority.ParseActiveRecord(currentAuthorityRecord)));
        currentAuthorityRecord = wrongKey.ExportSubjectPublicKeyInfo();
        Reject(() => authorityBoundVerifier.RequireModules([]));
        Console.WriteLine("PASS: one-way approval stop and installed verifier authority-change refusal (synthetic)");
        using var recoveryRoot = RSA.Create(3072);
        using var recoveredBaselineKey = RSA.Create(3072);
        using var recoveredApprovalKey = RSA.Create(3072);
        byte[] recoveryPublic = recoveryRoot.ExportSubjectPublicKeyInfo();
        byte[] recoveryAnchor = SHA256.HashData(recoveryPublic);
        byte[] recoveredBaselinePublic = recoveredBaselineKey.ExportSubjectPublicKeyInfo();
        byte[] recoveredApprovalPublic = recoveredApprovalKey.ExportSubjectPublicKeyInfo();
        byte[] recoveryPayload = Payload(sequence: 10);
        byte[] recoveryBaselineSignature = recoveredBaselineKey.SignData(recoveryPayload,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        string[] recoveryHistory = new[] { CefSystemBaselineTrustState.KeyId(publicKey),
            CefSystemBaselineTrustState.KeyId(approvalPublic) }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var expectedRecovered = CefSystemBaselineTrustState.CreateVerifiedRecord(recoveryPayload,
            recoveredBaselinePublic, 10, recoveryHistory);
        byte[] recoveryDigest = SHA256.HashData([.. "VEIL-CEF-APPROVAL-RECOVERY-V1"u8,
            .. SHA256.HashData(stoppedAuthority), .. SHA256.HashData(nextState.CopyRecord()),
            .. SHA256.HashData(recoveredApprovalPublic), .. SHA256.HashData(expectedRecovered.CopyRecord())]);
        byte[] recoverySignature = recoveryRoot.SignHash(recoveryDigest,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        CefApprovalAuthorityRecovery Recover(byte[] stopped, CefSystemBaselineTrustState current,
            byte[] baselineKey, byte[] approvalCandidate, byte[] root, byte[] anchorValue, byte[] proof) =>
            CefApprovalAuthorityRecovery.Prepare(stopped, current, recoveryPayload, recoveryBaselineSignature,
                baselineKey, approvalCandidate, root, anchorValue, proof, host);
        var recoveryPlan = Recover(stoppedAuthority, nextState, recoveredBaselinePublic,
            recoveredApprovalPublic, recoveryPublic, recoveryAnchor, recoverySignature);
        var recovered = CefSystemBaselineTrustState.Parse(recoveryPlan.RequireBaselineStage(stoppedAuthority, nextState, host));
        expectedRecovered.RequireSame(recovered);
        if (!recovered.CopyRetiredKeyIds().SequenceEqual(recoveryHistory))
            throw new InvalidOperationException("Recovery lost compromised key identities.");
        if (!recoveryPlan.RequireAuthorityStage(stoppedAuthority, recovered, host).SequenceEqual(recoveredApprovalPublic) ||
            recoveryPlan.Classify(recoveredApprovalPublic, recovered, host) != CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed ||
            recoveryPlan.Classify(recoveredApprovalPublic, nextState, host) != CefApprovalRecoveryStatus.UnexpectedRequiresReview ||
            recoveryPlan.Classify(stoppedAuthority, null, host) != CefApprovalRecoveryStatus.UnexpectedRequiresReview)
            throw new InvalidOperationException("Authority recovery ordering regression.");
        Reject(() => recoveryPlan.RequireAuthorityStage(stoppedAuthority, nextState, host));
        Reject(() => recoveryPlan.RequireBaselineStage(stoppedAuthority, recovered, host));
        Reject(() => recoveryPlan.RequireBaselineStage(stoppedAuthority, nextState, host with { UpdateRevision = 1235 }));
        Reject(() => Recover(approvalPublic, nextState, recoveredBaselinePublic, recoveredApprovalPublic,
            recoveryPublic, recoveryAnchor, recoverySignature));
        Reject(() => Recover(stoppedAuthority, enrolled, recoveredBaselinePublic, recoveredApprovalPublic,
            recoveryPublic, recoveryAnchor, recoverySignature));
        Reject(() => Recover(stoppedAuthority, nextState, recoveredBaselinePublic, recoveredApprovalPublic,
            recoveryPublic, new byte[32], recoverySignature));
        Reject(() => Recover(stoppedAuthority, nextState, recoveredBaselinePublic, recoveredApprovalPublic,
            recoveryPublic, recoveryAnchor, approvalKey.SignHash(recoveryDigest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        Reject(() => Recover(stoppedAuthority, nextState, recoveredBaselinePublic, recoveredApprovalPublic,
            approvalPublic, approvalAnchor, approvalKey.SignHash(recoveryDigest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        Reject(() => Recover(stoppedAuthority, nextState, recoveredBaselinePublic, approvalPublic,
            recoveryPublic, recoveryAnchor, recoverySignature));
        Reject(() => Recover(stoppedAuthority, nextState, recoveredBaselinePublic, recoveredBaselinePublic,
            recoveryPublic, recoveryAnchor, recoverySignature));
        Reject(() => recovered.CreateSuccessor(Payload(sequence: 11), approvalPublic, 11));
        Reject(() => authority.RequireIndependent(recovered));
        // Fault injection uses in-memory records only; no HKLM writes.
        // Each explicit restart uses the original authenticated plan.
        string[] expectedOperations = ["flush-authority", "write-baseline", "flush-baseline",
            "write-authority", "flush-authority"];
        for (int failAt = 0; failAt <= expectedOperations.Length; failAt++)
        {
            byte[] simulatedAuthority = stoppedAuthority;
            var simulatedBaseline = nextState;
            var operations = new List<string>();
            void Step(string operation) {
                operations.Add(operation);
                if (operations.Count == failAt) throw new System.IO.IOException("Synthetic recovery fault.");
            }
            (byte[], CefSystemBaselineTrustState, CefSystemBaselineHost) Read() =>
                (simulatedAuthority, simulatedBaseline, host);
            void WriteBaseline(byte[] bytes) {
                simulatedBaseline = CefSystemBaselineTrustState.Parse(bytes);
                Step("write-baseline");
            }
            void WriteAuthority(byte[] bytes) {
                expectedRecovered.RequireSame(simulatedBaseline);
                simulatedAuthority = bytes;
                Step("write-authority");
            }
            bool failed = false;
            try {
                recoveryPlan.ExecuteOrdered(Read, WriteBaseline, () => Step("flush-baseline"),
                    WriteAuthority, () => Step("flush-authority"));
            }
            catch (System.IO.IOException) { failed = true; }
            if (failed != (failAt != 0) ||
                !operations.SequenceEqual(expectedOperations.Take(failAt == 0 ? expectedOperations.Length : failAt)))
                throw new InvalidOperationException("Recovery fault did not stop at the expected operation.");
            var interrupted = recoveryPlan.Classify(simulatedAuthority, simulatedBaseline, host);
            if (interrupted == CefApprovalRecoveryStatus.UnexpectedRequiresReview)
                throw new InvalidOperationException("Unexpected simulated interrupted recovery pair.");
            operations.Clear();
            // Disable injection and explicitly resume/confirm the same plan.
            recoveryPlan.ExecuteOrdered(Read,
                bytes => { simulatedBaseline = CefSystemBaselineTrustState.Parse(bytes); operations.Add("write-baseline"); },
                () => operations.Add("flush-baseline"),
                bytes => { expectedRecovered.RequireSame(simulatedBaseline); simulatedAuthority = bytes; operations.Add("write-authority"); },
                () => operations.Add("flush-authority"));
            if (recoveryPlan.Classify(simulatedAuthority, simulatedBaseline, host) != CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed)
                throw new InvalidOperationException("Recovery resume regression.");
            if (interrupted != CefApprovalRecoveryStatus.StoppedOriginal && operations.Contains("write-baseline") ||
                interrupted == CefApprovalRecoveryStatus.IntendedPairPresentUnconfirmed && operations.Contains("write-authority"))
                throw new InvalidOperationException("Recovery rewrote an already completed stage.");
        }
        int unexpectedWrites = 0;
        Reject(() => recoveryPlan.ExecuteOrdered(() => (recoveredApprovalPublic, nextState, host),
            _ => unexpectedWrites++, () => unexpectedWrites++, _ => unexpectedWrites++, () => unexpectedWrites++));
        if (unexpectedWrites != 0) throw new InvalidOperationException("Unexpected recovery pair modified.");
        Console.WriteLine("PASS: recovery ordered persistence, fault injection and explicit same-plan resume (synthetic)");
        Console.WriteLine("PASS: independently signed two-record authority recovery plan and fail-closed ordering");
        byte[] Approve(byte[] digest) => approvalKey.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        CefSystemBaselineTrustUpdate Rotate(CefSystemBaselineTrustState installed, byte[] payload,
            byte[] signature, byte[] newKey, byte[] digest, CefSystemBaselineHost expectedHost) =>
            CefSystemBaselineTrustUpdate.PrepareKeyRotation(installed, payload, signature, newKey,
                authority, Approve(digest), expectedHost);
        CefSystemBaselineTrustUpdate Revoke(CefSystemBaselineTrustState installed, byte[] digest,
            CefSystemBaselineHost expectedHost) =>
            CefSystemBaselineTrustUpdate.PrepareRevocation(installed, authority, Approve(digest), expectedHost);
        Reject(() => new CefBaselineApprovalAuthority(approvalPublic, new byte[32]));
        Reject(() => new CefBaselineApprovalAuthority(weakKey.ExportSubjectPublicKeyInfo(),
            SHA256.HashData(weakKey.ExportSubjectPublicKeyInfo())));
        using var rotatedKey = RSA.Create(3072);
        byte[] rotatedPublicKey = rotatedKey.ExportSubjectPublicKeyInfo();
        byte[] rotationPayload = Payload(sequence: 10);
        byte[] rotationSignature = rotatedKey.SignData(rotationPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        byte[] Approval(CefSystemBaselineTrustState from, CefSystemBaselineTrustState to) =>
            SHA256.HashData([.. "VEIL-CEF-BASELINE-KEY-ROTATION-V1"u8,
                .. SHA256.HashData(from.CopyRecord()), .. SHA256.HashData(to.CopyRecord())]);
        string retiredId = Convert.ToHexString(SHA256.HashData(signing.ExportParameters(false).Modulus!));
        var proposed = CefSystemBaselineTrustState.CreateVerifiedRecord(rotationPayload, rotatedPublicKey, 10, [retiredId]);
        byte[] approval = Approval(nextState, proposed);
        byte[] signedApproval = Approve(approval);
        byte[] badApproval = (byte[])signedApproval.Clone();
        badApproval[0] ^= 1;
        Reject(() => CefSystemBaselineTrustUpdate.PrepareKeyRotation(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, authority, badApproval, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareKeyRotation(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, authority, approval, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareKeyRotation(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, authority,
            signing.SignHash(approval, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareKeyRotation(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, authority,
            approvalKey.SignHash(approval, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), host));
        var oldAuthority = new CefBaselineApprovalAuthority(publicKey, SHA256.HashData(publicKey));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareKeyRotation(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, oldAuthority,
            signing.SignHash(approval, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host));
        var newAuthority = new CefBaselineApprovalAuthority(rotatedPublicKey, SHA256.HashData(rotatedPublicKey));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareKeyRotation(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, newAuthority,
            rotatedKey.SignHash(approval, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host));
        var rotation = Rotate(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, approval, host);
        var rotated = CefSystemBaselineTrustState.Parse(rotation.RequireExpectedAndCopyRecord(nextState, host));
        using var alternateApprovalKey = RSA.Create(3072);
        byte[] alternateApprovalPublic = alternateApprovalKey.ExportSubjectPublicKeyInfo();
        var alternateAuthority = new CefBaselineApprovalAuthority(alternateApprovalPublic,
            SHA256.HashData(alternateApprovalPublic));
        var sameAuthority = new CefBaselineApprovalAuthority(approvalPublic, approvalAnchor);
        authority.RequireSame(sameAuthority);
        Reject(() => authority.RequireSame(alternateAuthority));
        rotation.RequirePublicationAuthority(sameAuthority);
        Reject(() => rotation.RequirePublicationAuthority(alternateAuthority));
        Reject(() => rotation.RequirePublicationAuthority(oldAuthority));
        Reject(() => rotation.RequirePublicationAuthority(newAuthority));
        enrollment.RequirePublicationAuthority(authority);
        update.RequirePublicationAuthority(authority);
        Reject(() => enrollment.RequirePublicationAuthority(oldAuthority));
        Reject(() => update.RequirePublicationAuthority(oldAuthority));
        Console.WriteLine("PASS: publication authority identity freshness and baseline key-role refusal");
        proposed.RequireSame(rotated);
        rotation.RequirePublished(rotated, host);
        Reject(() => rotation.RequireExpectedAndCopyRecord(enrolled, host));
        Reject(() => rotation.RequireExpectedAndCopyRecord(rotated, host));
        Reject(() => rotation.RequirePublished(nextState, host));
        Reject(() => Rotate(nextState, rotationPayload,
            rotationSignature, rotatedPublicKey, new byte[32], host));
        Reject(() => Rotate(enrolled, rotationPayload,
            rotationSignature, rotatedPublicKey, approval, host));
        Reject(() => Rotate(nextState, rotationPayload,
            Sign(rotationPayload), rotatedPublicKey, approval, host));
        Reject(() => Rotate(nextState, rotationPayload,
            Sign(rotationPayload), publicKey, Approval(nextState,
                CefSystemBaselineTrustState.CreateVerifiedRecord(rotationPayload, publicKey, 10)), host));
        Reject(() => Rotate(nextState, nextPayload,
            rotatedKey.SignData(nextPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            rotatedPublicKey, Approval(nextState,
                CefSystemBaselineTrustState.CreateVerifiedRecord(nextPayload, rotatedPublicKey, 9)), host));
        byte[] otherRotationPayload = Payload(sequence: 11);
        Reject(() => Rotate(nextState, otherRotationPayload,
            rotatedKey.SignData(otherRotationPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            rotatedPublicKey, approval, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareReplacement(rotated, otherRotationPayload,
            Sign(otherRotationPayload), host));
        var afterRotation = CefSystemBaselineTrustUpdate.PrepareReplacement(rotated, otherRotationPayload,
            rotatedKey.SignData(otherRotationPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host);
        var afterRotationState = CefSystemBaselineTrustState.Parse(afterRotation.RequireExpectedAndCopyRecord(rotated, host));
        if (afterRotationState.MinimumSequence != 11 || !afterRotationState.CopyRetiredKeyIds().SequenceEqual(new[] { retiredId }))
            throw new InvalidOperationException("New signing key or retained history regression.");
        // Even a separately approved rotation cannot reintroduce a retired modulus.
        Reject(() => Rotate(rotated, otherRotationPayload,
            Sign(otherRotationPayload), publicKey, new byte[32], host));
        Reject(() => rotated.CreateSuccessor(otherRotationPayload, publicKey, 11));
        var historyCopy = rotated.CopyRetiredKeyIds();
        historyCopy[0] = new string('0', 64);
        if (rotated.CopyRetiredKeyIds()[0] != retiredId)
            throw new InvalidOperationException("Mutable history escaped.");
        using var thirdKey = RSA.Create(3072);
        byte[] thirdPublic = thirdKey.ExportSubjectPublicKeyInfo();
        byte[] thirdPayload = Payload(sequence: 12);
        string secondId = Convert.ToHexString(SHA256.HashData(rotatedKey.ExportParameters(false).Modulus!));
        string[] expectedHistory = new[] { retiredId, secondId }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var thirdProposed = CefSystemBaselineTrustState.CreateVerifiedRecord(thirdPayload, thirdPublic, 12, expectedHistory);
        var thirdUpdate = Rotate(afterRotationState, thirdPayload,
            thirdKey.SignData(thirdPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            thirdPublic, Approval(afterRotationState, thirdProposed), host);
        var thirdState = CefSystemBaselineTrustState.Parse(thirdUpdate.RequireExpectedAndCopyRecord(afterRotationState, host));
        thirdUpdate.RequirePublicationAuthority(authority);
        Reject(() => thirdUpdate.RequirePublicationAuthority(oldAuthority));
        thirdProposed.RequireSame(thirdState);
        Reject(() => thirdState.CreateSuccessor(Payload(sequence: 13), publicKey, 13));
        Reject(() => thirdState.CreateSuccessor(Payload(sequence: 13), rotatedPublicKey, 13));
        if (!thirdState.CreateRevokedRecord().CopyRetiredKeyIds().SequenceEqual(expectedHistory))
            throw new InvalidOperationException("Revocation lost retired history.");
        Reject(() => CefSystemBaselineTrustState.CreateVerifiedRecord(thirdPayload, thirdPublic, 12, [retiredId, retiredId]));
        Reject(() => CefSystemBaselineTrustState.CreateVerifiedRecord(thirdPayload, thirdPublic, 12, expectedHistory.Reverse().ToArray()));
        Reject(() => CefSystemBaselineTrustState.CreateVerifiedRecord(thirdPayload, thirdPublic, 12,
            [CefSystemBaselineTrustState.KeyId(thirdPublic)]));
        byte[] v2Record = thirdState.CopyRecord();
        Reject(() => CefSystemBaselineTrustState.Parse(v2Record[..^1]));
        Reject(() => CefSystemBaselineTrustState.Parse([.. v2Record, 0]));
        var oversizedHistory = Enumerable.Range(0, 129).Select(i => i.ToString("X64")).ToArray();
        Reject(() => CefSystemBaselineTrustState.CreateVerifiedRecord(thirdPayload, thirdPublic, 12, oversizedHistory));
        var fullHistory = CefSystemBaselineTrustState.CreateVerifiedRecord(thirdPayload, thirdPublic, 12, oversizedHistory[..128]);
        Reject(() => fullHistory.CreateSuccessor(Payload(sequence: 13), rotatedPublicKey, 13));
        Console.WriteLine("PASS: retained RSA modulus history, rotation reuse and malformed history refusal");
        byte[] revocationApproval = SHA256.HashData([.. "VEIL-CEF-BASELINE-KEY-REVOCATION-V1"u8,
            .. SHA256.HashData(nextState.CopyRecord())]);
        Reject(() => CefSystemBaselineTrustUpdate.PrepareRevocation(nextState, authority, signedApproval, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareRevocation(nextState, oldAuthority,
            signing.SignHash(revocationApproval, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host));
        byte[] rotatedRevokeDigest = SHA256.HashData([.. "VEIL-CEF-BASELINE-KEY-REVOCATION-V1"u8,
            .. SHA256.HashData(rotated.CopyRecord())]);
        Reject(() => CefSystemBaselineTrustUpdate.PrepareRevocation(rotated, oldAuthority,
            signing.SignHash(rotatedRevokeDigest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), host));
        var revoke = Revoke(nextState, revocationApproval, host);
        Console.WriteLine("PASS: independent RSA-PSS approval, domain separation and current/new/retired key refusal");
        var revoked = CefSystemBaselineTrustState.Parse(revoke.RequireExpectedAndCopyRecord(nextState, host));
        revoke.RequirePublicationAuthority(sameAuthority);
        Reject(() => revoke.RequirePublicationAuthority(alternateAuthority));
        if (!revoked.Revoked || revoked.MinimumSequence != nextState.MinimumSequence ||
            !revoked.CopyPublicKey().SequenceEqual(publicKey))
            throw new InvalidOperationException("Revocation record regression.");
        revoke.RequirePublished(revoked, host);
        Reject(() => revoked.RequirePayload(nextPayload));
        Reject(() => revoke.RequireExpectedAndCopyRecord(revoked, host));
        Reject(() => revoke.RequirePublished(nextState, host));
        Reject(() => Revoke(enrolled, revocationApproval, host));
        Reject(() => Revoke(nextState, new byte[32], host));
        Reject(() => Revoke(revoked, revocationApproval, host));
        Reject(() => CefSystemBaselineTrustUpdate.PrepareReplacement(revoked, rotationPayload, Sign(rotationPayload), host));
        Reject(() => Rotate(revoked, rotationPayload,
            rotationSignature, rotatedPublicKey, approval, host));
        var resume = Rotate(revoked, rotationPayload,
            rotationSignature, rotatedPublicKey, Approval(revoked, proposed), host);
        var resumed = CefSystemBaselineTrustState.Parse(resume.RequireExpectedAndCopyRecord(revoked, host));
        if (resumed.Revoked) throw new InvalidOperationException("Approved rotation did not restore active trust.");
        proposed.RequireSame(resumed);
        // Use actual host metadata for the disconnected live snapshot test.
        var actualInstalled = CefSystemBaselineTrustState.CreateVerifiedRecord(actualPayload, publicKey, 7);
        var liveState = actualInstalled;
        var liveVerifier = CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(actualPayload, Sign(actualPayload),
            actualInstalled, () => liveState);
        liveVerifier.RequireModules([]);
        liveState = actualInstalled.CreateRevokedRecord();
        Reject(() => liveVerifier.RequireModules([]));
        Reject(() => CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(actualPayload, Sign(actualPayload),
            liveState, () => liveState));
        Console.WriteLine("PASS: explicit signing-key revocation, active snapshot refusal and independently approved recovery");
        Console.WriteLine("PASS: independently approved exact-record key rotation, stale and old-key refusal");
        var mismatched = CefSystemBaselineTrustState.CreateVerifiedRecord(actualPayload, publicKey, 6);
        Reject(() => CefSignedSystemModuleBaseline.VerifyTrustedSnapshot(actualPayload, Sign(actualPayload),
            mismatched, () => mismatched));
        static byte[] MutexDescriptor(string sddl)
        {
            var raw = new System.Security.AccessControl.RawSecurityDescriptor(sddl);
            byte[] bytes = new byte[raw.BinaryLength];
            raw.GetBinaryForm(bytes, 0);
            return bytes;
        }
        CefSystemBaselineWriterLock.RequireDescriptor(MutexDescriptor("O:BAG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)"));
        Reject(() => CefSystemBaselineWriterLock.RequireDescriptor(MutexDescriptor("O:BAG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)(A;;0x1f0001;;;BU)")));
        Reject(() => CefSystemBaselineWriterLock.RequireDescriptor(MutexDescriptor("O:BUG:BAD:P(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)")));
        Reject(() => CefSystemBaselineWriterLock.RequireDescriptor(MutexDescriptor("O:BAG:BAD:(A;;0x1f0001;;;SY)(A;;0x1f0001;;;BA)")));
        Console.WriteLine("PASS: baseline writer mutex descriptor refusal (synthetic, no machine registry writes)");
        Console.WriteLine("PASS: independently anchored enrollment, strictly increasing signed update and stale-state refusal");
        var wrongHost = Payload(build: actual.Build, revision: actual.UpdateRevision == 0 ? 1 : 0);
        Reject(() => CefSignedSystemModuleBaseline.VerifyCurrentHost(wrongHost, Sign(wrongHost), publicKey, 7));
        Console.WriteLine("PASS: native Windows build and HKLM revision binding, host-change refusal");
        Console.WriteLine("PASS: signed system module baseline, host binding, sequence and malformed input refusal");
    }
}
