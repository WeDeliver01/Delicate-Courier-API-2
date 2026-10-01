"use client";

import { useEffect, useState } from "react";
import { useParams, useRouter } from "next/navigation";
import { ArrowLeft, Plus, Trash2, Save, Package, Tag } from "lucide-react";
import { AppLayout } from '@/components/layout/app-layout'
import api from '@/lib/api'

interface MappingRule {
  packageMappingRuleId: number;
  keyword: string;
  matchType: string;
  priority: number;
  isActive: boolean;
}

interface PackageType {
  packageTypeId: number;
  storeId: number;
  name: string;
  description: string | null;
  lengthCm: number;
  widthCm: number;
  heightCm: number;
  defaultWeightKg: number;
  maxWeightKg: number | null;
  isDefault: boolean;
  isActive: boolean;
  sortOrder: number;
  mappingRules: MappingRule[];
}

interface NewPackageType {
  name: string;
  description: string;
  lengthCm: number;
  widthCm: number;
  heightCm: number;
  defaultWeightKg: number;
  maxWeightKg: number | null;
  isDefault: boolean;
  sortOrder: number;
}

const emptyPackageType: NewPackageType = {
  name: "",
  description: "",
  lengthCm: 20,
  widthCm: 20,
  heightCm: 10,
  defaultWeightKg: 1,
  maxWeightKg: null,
  isDefault: false,
  sortOrder: 0,
};

export default function PackageTypesPage() {
  const params = useParams();
  const router = useRouter();
  const storeId = params.id as string;

  const [packageTypes, setPackageTypes] = useState<PackageType[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [storeName, setStoreName] = useState<string>("");

  const [showCreateModal, setShowCreateModal] = useState(false);
  const [showRuleModal, setShowRuleModal] = useState(false);
  const [selectedPackageTypeId, setSelectedPackageTypeId] = useState<number | null>(null);
  const [newPackageType, setNewPackageType] = useState<NewPackageType>(emptyPackageType);
  const [newRule, setNewRule] = useState({ keyword: "", matchType: "Contains", priority: 100 });
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    fetchPackageTypes();
    fetchStoreName();
  }, [storeId]);

  const fetchStoreName = async () => {
    try {
      const { data } = await api.get(`/stores/${storeId}`);
      setStoreName(data.storeName);
    } catch (err) {
      console.error("Failed to fetch store name:", err);
    }
  };

  const fetchPackageTypes = async () => {
    try {
      setLoading(true);
      setError(null);
      const { data } = await api.get<PackageType[]>(`/stores/${storeId}/packagetypes`);
      setPackageTypes(data);
    } catch (err: any) {
      setError(err?.response?.data?.message || err?.message || "Failed to fetch package types");
    } finally {
      setLoading(false);
    }
  };

  const handleCreatePackageType = async () => {
    if (!newPackageType.name.trim()) {
      alert("Package name is required");
      return;
    }

    setSaving(true);
    try {
      await api.post(`/stores/${storeId}/packagetypes`, newPackageType);
      setShowCreateModal(false);
      setNewPackageType(emptyPackageType);
      fetchPackageTypes();
    } catch (err: any) {
      alert(err?.response?.data?.message || err?.message || "Failed to create package type");
    } finally {
      setSaving(false);
    }
  };

  const handleDeletePackageType = async (packageTypeId: number) => {
    if (!confirm("Are you sure you want to delete this package type? All mapping rules will also be deleted.")) return;

    try {
      await api.delete(`/stores/${storeId}/packagetypes/${packageTypeId}`);
      fetchPackageTypes();
    } catch (err: any) {
      alert(err?.response?.data?.message || err?.message || "Failed to delete package type");
    }
  };

  const handleAddRule = async () => {
    if (!newRule.keyword.trim() || !selectedPackageTypeId) {
      alert("Keyword is required");
      return;
    }

    setSaving(true);
    try {
      await api.post(`/stores/${storeId}/packagetypes/${selectedPackageTypeId}/rules`, newRule);
      setShowRuleModal(false);
      setNewRule({ keyword: "", matchType: "Contains", priority: 100 });
      setSelectedPackageTypeId(null);
      fetchPackageTypes();
    } catch (err: any) {
      alert(err?.response?.data?.message || err?.message || "Failed to add mapping rule");
    } finally {
      setSaving(false);
    }
  };

  const handleDeleteRule = async (packageTypeId: number, ruleId: number) => {
    if (!confirm("Are you sure you want to delete this mapping rule?")) return;

    try {
      await api.delete(`/stores/${storeId}/packagetypes/${packageTypeId}/rules/${ruleId}`);
      fetchPackageTypes();
    } catch (err: any) {
      alert(err?.response?.data?.message || err?.message || "Failed to delete mapping rule");
    }
  };

  const openRuleModal = (packageTypeId: number) => {
    setSelectedPackageTypeId(packageTypeId);
    setShowRuleModal(true);
  };

  if (loading) {
    return (
      <AppLayout>
        <div className="p-4 sm:p-6 lg:p-8 min-h-screen bg-gray-50 dark:bg-gray-950">
          <div className="animate-pulse max-w-5xl mx-auto">
            <div className="h-8 bg-gray-200 dark:bg-gray-800 rounded w-1/4 mb-6"></div>
            <div className="h-64 bg-gray-200 dark:bg-gray-800 rounded"></div>
          </div>
        </div>
      </AppLayout>
    );
  }

  return (
    <AppLayout>
      <div className="p-4 sm:p-6 lg:p-8 min-h-screen bg-gray-50 dark:bg-gray-950">
        <div className="max-w-5xl mx-auto">
          {/* Header */}
          <div className="mb-6">
            <button
              onClick={() => router.push(`/stores/${storeId}`)}
              className="flex items-center text-sm text-gray-500 hover:text-gray-900 dark:text-gray-400 dark:hover:text-white mb-4 transition-colors"
            >
              <ArrowLeft className="w-4 h-4 mr-1" />
              Back to Store
            </button>

            <div className="flex flex-col gap-3 sm:flex-row sm:justify-between sm:items-center">
              <div>
                <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 dark:text-white">Package Types</h1>
                <p className="text-sm sm:text-base text-gray-600 dark:text-gray-400 mt-1">
                  Configure packaging options for {storeName || "this store"}
                </p>
              </div>
              <button
                onClick={() => setShowCreateModal(true)}
                className="flex items-center justify-center gap-2 bg-blue-600 text-white px-5 py-2.5 rounded-lg hover:bg-blue-700 transition-colors text-sm font-medium w-full sm:w-auto"
              >
                <Plus className="w-4 h-4" />
                Add Package Type
              </button>
            </div>
          </div>

          {/* Error */}
          {error && (
            <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 text-red-700 dark:text-red-200 px-4 py-3 rounded-lg mb-6 text-sm">
              {error}
            </div>
          )}

          {/* Empty State */}
          {packageTypes.length === 0 && !error && (
            <div className="bg-white dark:bg-gray-800 rounded-xl border border-gray-200 dark:border-gray-700 p-8 sm:p-12 text-center">
              <div className="w-16 h-16 bg-blue-50 dark:bg-blue-900/30 rounded-full flex items-center justify-center mx-auto mb-4">
                <Package className="w-8 h-8 text-blue-600 dark:text-blue-400" />
              </div>
              <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">No package types configured</h3>
              <p className="text-gray-600 dark:text-gray-400 mb-6 max-w-md mx-auto text-sm">
                Create package types with dimensions and add keyword rules to automatically match products to the right packaging.
              </p>
              <button
                onClick={() => setShowCreateModal(true)}
                className="inline-flex items-center gap-2 bg-blue-600 text-white px-5 py-2.5 rounded-lg hover:bg-blue-700 text-sm font-medium"
              >
                <Plus className="w-4 h-4" />
                Create Your First Package Type
              </button>
            </div>
          )}

          {/* Package Types List */}
          <div className="space-y-4">
            {packageTypes.map((pkg) => (
              <div key={pkg.packageTypeId} className="bg-white dark:bg-gray-800 rounded-xl border border-gray-200 dark:border-gray-700 overflow-hidden">
                {/* Package Header */}
                <div className="p-4 sm:p-5 border-b border-gray-100 dark:border-gray-700 bg-gray-50/50 dark:bg-gray-800/50">
                  <div className="flex justify-between items-start">
                    <div className="flex items-start gap-3">
                      <div className="w-10 h-10 bg-blue-50 dark:bg-blue-900/30 rounded-lg flex items-center justify-center flex-shrink-0 mt-0.5">
                        <Package className="w-5 h-5 text-blue-600 dark:text-blue-400" />
                      </div>
                      <div>
                        <h3 className="font-semibold text-gray-900 dark:text-white flex flex-wrap items-center gap-2">
                          {pkg.name}
                          {pkg.isDefault && (
                            <span className="text-xs bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-300 px-2 py-0.5 rounded-full font-medium">
                              Default
                            </span>
                          )}
                          {!pkg.isActive && (
                            <span className="text-xs bg-gray-100 dark:bg-gray-700 text-gray-600 dark:text-gray-300 px-2 py-0.5 rounded-full font-medium">
                              Inactive
                            </span>
                          )}
                        </h3>
                        {pkg.description && (
                          <p className="text-sm text-gray-500 dark:text-gray-400 mt-0.5">{pkg.description}</p>
                        )}
                      </div>
                    </div>
                    <button
                      onClick={() => handleDeletePackageType(pkg.packageTypeId)}
                      className="text-gray-400 hover:text-red-600 dark:hover:text-red-400 p-1.5 rounded-lg hover:bg-red-50 dark:hover:bg-red-900/20 transition-colors"
                      title="Delete package type"
                    >
                      <Trash2 className="w-4 h-4" />
                    </button>
                  </div>

                  {/* Dimensions - cards on mobile, inline on desktop */}
                  <div className="mt-3 grid grid-cols-2 sm:flex sm:flex-wrap gap-2 sm:gap-4 text-sm">
                    <div className="bg-white dark:bg-gray-700/50 rounded-lg px-3 py-2 sm:bg-transparent sm:p-0">
                      <span className="text-gray-400 dark:text-gray-500 text-xs sm:text-sm block sm:inline">Dimensions </span>
                      <span className="font-medium text-gray-900 dark:text-white">
                        {pkg.lengthCm} x {pkg.widthCm} x {pkg.heightCm} cm
                      </span>
                    </div>
                    <div className="bg-white dark:bg-gray-700/50 rounded-lg px-3 py-2 sm:bg-transparent sm:p-0">
                      <span className="text-gray-400 dark:text-gray-500 text-xs sm:text-sm block sm:inline">Weight </span>
                      <span className="font-medium text-gray-900 dark:text-white">{pkg.defaultWeightKg} kg</span>
                    </div>
                    {pkg.maxWeightKg && (
                      <div className="bg-white dark:bg-gray-700/50 rounded-lg px-3 py-2 sm:bg-transparent sm:p-0">
                        <span className="text-gray-400 dark:text-gray-500 text-xs sm:text-sm block sm:inline">Max </span>
                        <span className="font-medium text-gray-900 dark:text-white">{pkg.maxWeightKg} kg</span>
                      </div>
                    )}
                  </div>
                </div>

                {/* Mapping Rules */}
                <div className="p-4 sm:p-5">
                  <div className="flex justify-between items-center mb-3">
                    <h4 className="text-sm font-medium text-gray-700 dark:text-gray-300 flex items-center gap-2">
                      <Tag className="w-4 h-4" />
                      Mapping Rules ({pkg.mappingRules.length})
                    </h4>
                    <button
                      onClick={() => openRuleModal(pkg.packageTypeId)}
                      className="text-sm text-blue-600 dark:text-blue-400 hover:text-blue-800 dark:hover:text-blue-300 flex items-center gap-1 font-medium transition-colors"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      Add Rule
                    </button>
                  </div>

                  {pkg.mappingRules.length === 0 ? (
                    <p className="text-sm text-gray-400 dark:text-gray-500 italic py-2">
                      No mapping rules. Products won&apos;t automatically use this package type.
                    </p>
                  ) : (
                    <div className="flex flex-wrap gap-2">
                      {pkg.mappingRules.map((rule) => (
                        <div
                          key={rule.packageMappingRuleId}
                          className="inline-flex items-center gap-2 bg-blue-50 dark:bg-blue-900/20 text-blue-800 dark:text-blue-300 px-3 py-1.5 rounded-lg text-sm border border-blue-100 dark:border-blue-800/30"
                        >
                          <span className="font-medium">&quot;{rule.keyword}&quot;</span>
                          <span className="text-blue-400 dark:text-blue-500 text-xs">
                            {rule.matchType} · P{rule.priority}
                          </span>
                          <button
                            onClick={() => handleDeleteRule(pkg.packageTypeId, rule.packageMappingRuleId)}
                            className="text-blue-400 hover:text-red-500 dark:hover:text-red-400 transition-colors ml-1"
                          >
                            <Trash2 className="w-3 h-3" />
                          </button>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>

          {/* Create Package Type Modal */}
          {showCreateModal && (
            <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50 p-4">
              <div className="bg-white dark:bg-gray-800 rounded-xl shadow-xl w-full max-w-lg max-h-[90vh] overflow-y-auto">
                <div className="p-5 sm:p-6 border-b border-gray-200 dark:border-gray-700">
                  <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Create Package Type</h2>
                  <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">Define dimensions and weight for this packaging option.</p>
                </div>
                <div className="p-5 sm:p-6 space-y-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">
                      Name <span className="text-red-500">*</span>
                    </label>
                    <input
                      type="text"
                      value={newPackageType.name}
                      onChange={(e) => setNewPackageType({ ...newPackageType, name: e.target.value })}
                      placeholder="e.g., Medium Cheesecake Box"
                      className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />
                  </div>

                  <div>
                    <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Description</label>
                    <input
                      type="text"
                      value={newPackageType.description}
                      onChange={(e) => setNewPackageType({ ...newPackageType, description: e.target.value })}
                      placeholder="Optional description"
                      className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />
                  </div>

                  <div className="grid grid-cols-3 gap-3">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Length (cm)</label>
                      <input
                        type="number"
                        value={newPackageType.lengthCm}
                        onChange={(e) => setNewPackageType({ ...newPackageType, lengthCm: parseInt(e.target.value) || 0 })}
                        className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Width (cm)</label>
                      <input
                        type="number"
                        value={newPackageType.widthCm}
                        onChange={(e) => setNewPackageType({ ...newPackageType, widthCm: parseInt(e.target.value) || 0 })}
                        className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Height (cm)</label>
                      <input
                        type="number"
                        value={newPackageType.heightCm}
                        onChange={(e) => setNewPackageType({ ...newPackageType, heightCm: parseInt(e.target.value) || 0 })}
                        className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      />
                    </div>
                  </div>

                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Default Weight (kg)</label>
                      <input
                        type="number"
                        step="0.1"
                        value={newPackageType.defaultWeightKg}
                        onChange={(e) => setNewPackageType({ ...newPackageType, defaultWeightKg: parseFloat(e.target.value) || 0 })}
                        className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Max Weight (kg)</label>
                      <input
                        type="number"
                        step="0.1"
                        value={newPackageType.maxWeightKg || ""}
                        onChange={(e) => setNewPackageType({ ...newPackageType, maxWeightKg: e.target.value ? parseFloat(e.target.value) : null })}
                        placeholder="Optional"
                        className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                      />
                    </div>
                  </div>

                  <div className="flex items-center gap-2.5">
                    <input
                      type="checkbox"
                      id="isDefault"
                      checked={newPackageType.isDefault}
                      onChange={(e) => setNewPackageType({ ...newPackageType, isDefault: e.target.checked })}
                      className="rounded border-gray-300 dark:border-gray-600 text-blue-600 focus:ring-blue-500 w-4 h-4"
                    />
                    <label htmlFor="isDefault" className="text-sm text-gray-700 dark:text-gray-300">
                      Set as default package (used when no rules match)
                    </label>
                  </div>
                </div>
                <div className="p-5 sm:p-6 border-t border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800/50 flex flex-col-reverse sm:flex-row justify-end gap-3">
                  <button
                    onClick={() => {
                      setShowCreateModal(false);
                      setNewPackageType(emptyPackageType);
                    }}
                    className="px-4 py-2.5 text-sm text-gray-700 dark:text-gray-300 hover:text-gray-900 dark:hover:text-white border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={handleCreatePackageType}
                    disabled={saving}
                    className="flex items-center justify-center gap-2 bg-blue-600 text-white px-5 py-2.5 rounded-lg hover:bg-blue-700 disabled:opacity-50 text-sm font-medium transition-colors"
                  >
                    <Save className="w-4 h-4" />
                    {saving ? "Creating..." : "Create Package Type"}
                  </button>
                </div>
              </div>
            </div>
          )}

          {/* Add Rule Modal */}
          {showRuleModal && (
            <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50 p-4">
              <div className="bg-white dark:bg-gray-800 rounded-xl shadow-xl w-full max-w-lg">
                <div className="p-5 sm:p-6 border-b border-gray-200 dark:border-gray-700">
                  <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Add Mapping Rule</h2>
                  <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
                    Products matching this rule will use this package type.
                  </p>
                </div>
                <div className="p-5 sm:p-6 space-y-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">
                      Keyword <span className="text-red-500">*</span>
                    </label>
                    <input
                      type="text"
                      value={newRule.keyword}
                      onChange={(e) => setNewRule({ ...newRule, keyword: e.target.value })}
                      placeholder="e.g., Cheesecake"
                      className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />
                    <p className="text-xs text-gray-400 dark:text-gray-500 mt-1.5">
                      Enter a word or phrase to match in product names
                    </p>
                  </div>

                  <div>
                    <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Match Type</label>
                    <select
                      value={newRule.matchType}
                      onChange={(e) => setNewRule({ ...newRule, matchType: e.target.value })}
                      className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    >
                      <option value="Contains">Contains (product name includes keyword)</option>
                      <option value="StartsWith">Starts With (product name begins with keyword)</option>
                      <option value="Exact">Exact Match (product name equals keyword)</option>
                    </select>
                  </div>

                  <div>
                    <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1.5">Priority</label>
                    <input
                      type="number"
                      value={newRule.priority}
                      onChange={(e) => setNewRule({ ...newRule, priority: parseInt(e.target.value) || 100 })}
                      className="w-full border border-gray-300 dark:border-gray-600 rounded-lg px-3 py-2.5 text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                    />
                    <p className="text-xs text-gray-400 dark:text-gray-500 mt-1.5">
                      Lower number = higher priority. If a product matches multiple rules, the lowest priority wins.
                    </p>
                  </div>
                </div>
                <div className="p-5 sm:p-6 border-t border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-800/50 flex flex-col-reverse sm:flex-row justify-end gap-3">
                  <button
                    onClick={() => {
                      setShowRuleModal(false);
                      setNewRule({ keyword: "", matchType: "Contains", priority: 100 });
                      setSelectedPackageTypeId(null);
                    }}
                    className="px-4 py-2.5 text-sm text-gray-700 dark:text-gray-300 hover:text-gray-900 dark:hover:text-white border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={handleAddRule}
                    disabled={saving}
                    className="flex items-center justify-center gap-2 bg-blue-600 text-white px-5 py-2.5 rounded-lg hover:bg-blue-700 disabled:opacity-50 text-sm font-medium transition-colors"
                  >
                    <Plus className="w-4 h-4" />
                    {saving ? "Adding..." : "Add Rule"}
                  </button>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>
    </AppLayout>
  );
}